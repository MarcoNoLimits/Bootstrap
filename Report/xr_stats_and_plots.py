#!/usr/bin/env python3
"""
XR performance statistical analysis for Unity (HoloLens2) vs WebXR.

Outputs:
- Report/xr_statistical_table.csv
- Report/xr_statistical_results.md
- Report/xr_fps_comparison.png
- Report/xr_p95_comparison.png
- Report/xr_unity_metrics.png
- Report/xr_webxr_metrics.png
"""

from __future__ import annotations

import math
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd


ROOT = Path(__file__).resolve().parent
INPUT_CSV = ROOT / "xr_perf_runs.csv"
OUTPUT_TABLE = ROOT / "xr_statistical_table.csv"
OUTPUT_MD = ROOT / "xr_statistical_results.md"
OUTPUT_FPS_PNG = ROOT / "xr_fps_comparison.png"
OUTPUT_P95_PNG = ROOT / "xr_p95_comparison.png"
OUTPUT_UNITY_METRICS_PNG = ROOT / "xr_unity_metrics.png"
OUTPUT_WEBXR_METRICS_PNG = ROOT / "xr_webxr_metrics.png"

UNITY_LABEL = "Unity_HoloLens2"
WEBXR_LABEL = "WebXR"
RNG = np.random.default_rng(42)

plt.rcParams.update(
    {
        "font.family": "serif",
        "font.serif": ["Times New Roman", "Times", "DejaVu Serif"],
        "font.size": 12,
    }
)


def mean_std_ci(values: np.ndarray) -> tuple[float, float | None, tuple[float, float] | None]:
    mean_val = float(np.mean(values))
    if len(values) < 2:
        return mean_val, None, None

    std_val = float(np.std(values, ddof=1))
    # Non-parametric bootstrap CI for mean
    draws = 10000
    sample_count = len(values)
    boot_means = np.empty(draws, dtype=float)
    for i in range(draws):
        resample = RNG.choice(values, size=sample_count, replace=True)
        boot_means[i] = np.mean(resample)
    low, high = np.percentile(boot_means, [2.5, 97.5])
    return mean_val, std_val, (float(low), float(high))


def permutation_pvalue(a: np.ndarray, b: np.ndarray, n_perm: int = 20000) -> float | None:
    if len(a) < 2 or len(b) < 2:
        return None

    observed = abs(float(np.mean(a) - np.mean(b)))
    combined = np.concatenate([a, b])
    n_a = len(a)

    count = 0
    for _ in range(n_perm):
        perm = RNG.permutation(combined)
        diff = abs(float(np.mean(perm[:n_a]) - np.mean(perm[n_a:])))
        if diff >= observed:
            count += 1
    # add-one smoothing
    return (count + 1.0) / (n_perm + 1.0)


def fmt_optional(value: float | None, digits: int = 3) -> str:
    if value is None or (isinstance(value, float) and math.isnan(value)):
        return "N/A"
    return f"{value:.{digits}f}"


def fmt_ci(ci: tuple[float, float] | None) -> str:
    if ci is None:
        return "N/A"
    return f"[{ci[0]:.3f}, {ci[1]:.3f}]"


def plot_metric(
    means: list[float],
    stds: list[float | None],
    ylabel: str,
    title: str,
    better_note: str,
    output_path: Path,
    ylim_pad_ratio: float = 0.25,
) -> None:
    labels = ["Unity-based XR", "WebXR"]
    colors = ["#2F64D6", "#F14141"]
    yerr = [0.0 if s is None else s for s in stds]

    plt.rcParams.update(
        {
            "font.size": 12,
            "axes.titlesize": 14,
            "axes.labelsize": 12,
            "xtick.labelsize": 12,
            "ytick.labelsize": 12,
        }
    )

    fig, ax = plt.subplots(figsize=(10, 7), facecolor="white")
    bars = ax.bar(labels, means, yerr=yerr, capsize=10, color=colors, width=0.56, edgecolor="black", linewidth=1.0)
    ax.set_facecolor("white")
    ax.set_ylabel(ylabel)
    ax.set_title(title, fontweight="bold")
    ax.grid(axis="y", alpha=0.25)

    max_val = max(means) if means else 1.0
    ax.set_ylim(0, max_val * (1 + ylim_pad_ratio))

    for i, bar in enumerate(bars):
        h = bar.get_height()
        ax.text(
            bar.get_x() + bar.get_width() / 2.0,
            h + max_val * 0.02,
            f"{means[i]:.2f}",
            ha="center",
            va="bottom",
            fontsize=12,
            fontweight="bold",
        )
        if stds[i] is not None:
            ax.text(
                bar.get_x() + bar.get_width() / 2.0,
                h + max_val * 0.10,
                f"std={stds[i]:.2f}",
                ha="center",
                va="bottom",
                fontsize=12,
                color="#333333",
            )
        else:
            ax.text(
                bar.get_x() + bar.get_width() / 2.0,
                h + max_val * 0.10,
                "std=N/A (n<2)",
                ha="center",
                va="bottom",
                fontsize=12,
                color="#555555",
            )

    ax.text(0.02, 0.02, better_note, transform=ax.transAxes, fontsize=12, fontweight="bold", color="#173970")
    fig.tight_layout()
    fig.savefig(output_path, dpi=220, facecolor="white")
    plt.close(fig)


def plot_platform_metrics(platform_name: str, means: dict[str, float], output_path: Path) -> None:
    metric_specs = [
        ("avg_fps", "Average FPS"),
        ("p95_frame_ms", "Frame-time P95 (ms)"),
        ("avg_frame_ms", "Average Frame-time (ms)"),
        ("updates_per_sec", "UI Updates / sec"),
    ]
    values = [means[m] for m, _ in metric_specs]

    x = np.arange(len(metric_specs), dtype=float)

    plt.rcParams.update(
        {
            "font.size": 12,
            "axes.titlesize": 14,
            "axes.labelsize": 12,
            "xtick.labelsize": 12,
            "ytick.labelsize": 12,
        }
    )

    fig, ax = plt.subplots(figsize=(11, 7), facecolor="white")
    color = "#319795" if platform_name == UNITY_LABEL else "#2b6cb0"
    bars = ax.bar(x, values, width=0.62, color=color, edgecolor="black", linewidth=1.0)
    ax.set_facecolor("white")
    title = "Hololens Performance" if platform_name == UNITY_LABEL else "WebXR Performance"
    ax.set_title(title)
    ax.set_ylabel("Metric value (native units)")
    ax.set_xticks(x)
    ax.set_xticklabels([label for _, label in metric_specs], rotation=10, ha="right")
    ax.grid(axis="y", alpha=0.25)

    top = max(values) if values else 1.0
    ax.set_ylim(0, top * 1.25)

    for b in bars:
        h = b.get_height()
        ax.text(
            b.get_x() + b.get_width() / 2.0,
            h + top * 0.015,
            f"{h:.2f}",
            ha="center",
            va="bottom",
            fontsize=12,
            fontweight="bold",
        )

    fig.tight_layout()
    fig.savefig(output_path, dpi=220, facecolor="white")
    plt.close(fig)


def main() -> None:
    if not INPUT_CSV.exists():
        raise FileNotFoundError(f"Missing input data file: {INPUT_CSV}")

    df = pd.read_csv(INPUT_CSV)
    required_cols = {"platform", "run_id", "avg_fps", "p95_frame_ms", "avg_frame_ms", "updates_per_sec", "duration_sec"}
    missing = required_cols.difference(df.columns)
    if missing:
        raise ValueError(f"Missing required columns: {sorted(missing)}")

    unity_df = df[df["platform"] == UNITY_LABEL]
    webxr_df = df[df["platform"] == WEBXR_LABEL]
    if unity_df.empty or webxr_df.empty:
        raise ValueError("Both Unity_HoloLens2 and WebXR rows are required in xr_perf_runs.csv")

    unity_fps = unity_df["avg_fps"].to_numpy(dtype=float)
    webxr_fps = webxr_df["avg_fps"].to_numpy(dtype=float)
    unity_p95 = unity_df["p95_frame_ms"].to_numpy(dtype=float)
    webxr_p95 = webxr_df["p95_frame_ms"].to_numpy(dtype=float)

    u_fps_mean, u_fps_std, u_fps_ci = mean_std_ci(unity_fps)
    w_fps_mean, w_fps_std, w_fps_ci = mean_std_ci(webxr_fps)
    u_p95_mean, u_p95_std, u_p95_ci = mean_std_ci(unity_p95)
    w_p95_mean, w_p95_std, w_p95_ci = mean_std_ci(webxr_p95)

    fps_pval = permutation_pvalue(unity_fps, webxr_fps)
    p95_pval = permutation_pvalue(unity_p95, webxr_p95)

    metric_names = ["avg_fps", "p95_frame_ms", "avg_frame_ms", "updates_per_sec", "duration_sec"]
    table_rows = []
    unity_means: dict[str, float] = {}
    webxr_means: dict[str, float] = {}
    for metric in metric_names:
        u_vals = unity_df[metric].to_numpy(dtype=float)
        w_vals = webxr_df[metric].to_numpy(dtype=float)
        u_mean, u_std, u_ci = mean_std_ci(u_vals)
        w_mean, w_std, w_ci = mean_std_ci(w_vals)
        pval = permutation_pvalue(u_vals, w_vals)
        unity_means[metric] = u_mean
        webxr_means[metric] = w_mean

        table_rows.append(
            {
                "metric": metric,
                "platform": UNITY_LABEL,
                "n_runs": len(u_vals),
                "mean": u_mean,
                "std": np.nan if u_std is None else u_std,
                "bootstrap_ci95_low": np.nan if u_ci is None else u_ci[0],
                "bootstrap_ci95_high": np.nan if u_ci is None else u_ci[1],
                "permutation_pvalue_vs_other_platform": np.nan if pval is None else pval,
            }
        )
        table_rows.append(
            {
                "metric": metric,
                "platform": WEBXR_LABEL,
                "n_runs": len(w_vals),
                "mean": w_mean,
                "std": np.nan if w_std is None else w_std,
                "bootstrap_ci95_low": np.nan if w_ci is None else w_ci[0],
                "bootstrap_ci95_high": np.nan if w_ci is None else w_ci[1],
                "permutation_pvalue_vs_other_platform": np.nan if pval is None else pval,
            }
        )

    pd.DataFrame(table_rows).to_csv(OUTPUT_TABLE, index=False)

    inferential_ok = len(unity_fps) >= 2 and len(webxr_fps) >= 2

    md_lines = [
        "# XR Statistical Analysis (Unity HoloLens2 vs WebXR)",
        "",
        "## Input Data Sufficiency",
        f"- Unity_HoloLens2: n={len(unity_fps)} runs",
        f"- WebXR: n={len(webxr_fps)} runs",
        "- Requirement for std + significance: at least 2 runs per platform (prefer 10+)",
        "",
        "## Numerical Results",
        "",
        "### Average FPS (higher is better)",
        f"- Unity_HoloLens2: mean={u_fps_mean:.3f}, std={fmt_optional(u_fps_std)}, 95% bootstrap CI={fmt_ci(u_fps_ci)}",
        f"- WebXR: mean={w_fps_mean:.3f}, std={fmt_optional(w_fps_std)}, 95% bootstrap CI={fmt_ci(w_fps_ci)}",
        f"- Permutation test p-value (difference in means): {fmt_optional(fps_pval, 5)}",
        "",
        "### Frame-time P95 in ms (lower is better)",
        f"- Unity_HoloLens2: mean={u_p95_mean:.3f}, std={fmt_optional(u_p95_std)}, 95% bootstrap CI={fmt_ci(u_p95_ci)}",
        f"- WebXR: mean={w_p95_mean:.3f}, std={fmt_optional(w_p95_std)}, 95% bootstrap CI={fmt_ci(w_p95_ci)}",
        f"- Permutation test p-value (difference in means): {fmt_optional(p95_pval, 5)}",
        "",
        "### Additional Metrics (from statistical table)",
        f"- Unity_HoloLens2 avg_frame_ms: {unity_means['avg_frame_ms']:.3f}; updates_per_sec: {unity_means['updates_per_sec']:.3f}; duration_sec: {unity_means['duration_sec']:.3f}",
        f"- WebXR avg_frame_ms: {webxr_means['avg_frame_ms']:.3f}; updates_per_sec: {webxr_means['updates_per_sec']:.3f}; duration_sec: {webxr_means['duration_sec']:.3f}",
        "",
        "## Decision",
    ]

    if inferential_ok:
        md_lines.append(
            "- Statistical conclusion should be based on p-values, confidence intervals, and standard deviation together; avoid relying on averages alone."
        )
    else:
        md_lines.append(
            "- Inferential conclusion is NOT valid yet because repeated runs are insufficient to estimate variance (std) robustly."
        )

    OUTPUT_MD.write_text("\n".join(md_lines) + "\n", encoding="utf-8")

    plot_metric(
        means=[u_fps_mean, w_fps_mean],
        stds=[u_fps_std, w_fps_std],
        ylabel="FPS",
        title="XR Average FPS (Unity HoloLens2 vs WebXR)",
        better_note="Higher is better",
        output_path=OUTPUT_FPS_PNG,
    )
    plot_metric(
        means=[u_p95_mean, w_p95_mean],
        stds=[u_p95_std, w_p95_std],
        ylabel="ms",
        title="XR Frame-time P95 (Unity HoloLens2 vs WebXR)",
        better_note="Lower is better",
        output_path=OUTPUT_P95_PNG,
    )
    plot_platform_metrics(UNITY_LABEL, unity_means, OUTPUT_UNITY_METRICS_PNG)
    plot_platform_metrics(WEBXR_LABEL, webxr_means, OUTPUT_WEBXR_METRICS_PNG)

    print(f"Wrote {OUTPUT_TABLE}")
    print(f"Wrote {OUTPUT_MD}")
    print(f"Wrote {OUTPUT_FPS_PNG}")
    print(f"Wrote {OUTPUT_P95_PNG}")
    print(f"Wrote {OUTPUT_UNITY_METRICS_PNG}")
    print(f"Wrote {OUTPUT_WEBXR_METRICS_PNG}")


if __name__ == "__main__":
    main()
