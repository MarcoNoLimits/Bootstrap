# BootstrapNMT Build & Deployment Commands Reference

This guide provides the exact PowerShell commands needed to compile the project and deploy it to the HoloLens 2 (`172.16.6.45`) using sideloading tools.

---

## 1. Incremental Build (MSBuild)

Run the fast-deploy PowerShell utility script from the repository root directory (`C:\GitHub\Bootstrap`) to build the solution in `Release` configuration for `ARM64`:

```powershell
powershell -File .\fast_deploy.ps1 -SolutionPath "UWP/BootstrapNMT.sln" -ProjectName "BootstrapNMT" -Configuration "Release" -Platform "ARM64"
```

---

## 2. Uninstall Previous Version from HoloLens 2

To prevent signature or version conflicts, uninstall the existing `BootstrapNMT` build from the device.

Run the following command in PowerShell:

```powershell
# Using the Windows 10/11 SDK WinAppDeployCmd utility
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\WinAppDeployCmd.exe" uninstall -package "BootstrapNMT_1.0.0.0_arm64__pzq3xp76mxafg" -ip 172.16.6.45
```

> [!NOTE]
> If version `10.0.26100.0` is not installed on your build machine, you can use the `10.0.22621.0` version instead:
> `& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\WinAppDeployCmd.exe" ...`

---

## 3. Install Updated Package onto HoloLens 2

Deploy the freshly built MSIX package together with its mandatory C++ runtime dependency.

Run the following command in PowerShell:

```powershell
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\WinAppDeployCmd.exe" install -file "C:\GitHub\Bootstrap\UWP\AppPackages\BootstrapNMT\BootstrapNMT_1.0.0.0_ARM64_Test\BootstrapNMT_1.0.0.0_ARM64.msix" -dependency "C:\GitHub\Bootstrap\UWP\AppPackages\BootstrapNMT\BootstrapNMT_1.0.0.0_ARM64_Test\Dependencies\ARM64\Microsoft.VCLibs.ARM64.14.00.appx" -ip 172.16.6.45
```

> [!TIP]
> Ensure the HoloLens is powered on, unlocked, and connected to the same local network (`172.16.6.45`) before launching the installation.
