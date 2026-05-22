#!/usr/bin/env python3
import os
import ssl
import sys
import getpass
import urllib.request
import urllib.parse
from urllib.error import HTTPError, URLError

import argparse

DEFAULT_IP = "172.16.6.45"
PACKAGE_FULL_NAME = "BootstrapNMT_1.0.0.0_arm64__pzq3xp76mxafg"
LOG_FILE_NAME = "asr_debug.log"

def main():
    print("=== HoloLens 2 Log Collector ===")
    
    parser = argparse.ArgumentParser(description="Pull logs from HoloLens 2.")
    parser.add_argument("--ip", help=f"HoloLens IP address (default: {DEFAULT_IP})")
    parser.add_argument("-u", "--username", help="Device Portal username")
    parser.add_argument("-p", "--password", help="Device Portal password")
    args = parser.parse_args()
    
    # 1. Get Device IP
    ip = args.ip
    if not ip:
        ip = input(f"Enter HoloLens IP address [{DEFAULT_IP}]: ").strip()
        if not ip:
            ip = DEFAULT_IP
    else:
        ip = ip.strip()
        
    # 2. Get Credentials
    username = args.username
    password = args.password
    if not username or not password:
        print("\nPlease enter your Windows Device Portal credentials:")
        if not username:
            username = input("Username: ").strip()
        if not password:
            password = getpass.getpass("Password: ")
    
    # 3. Construct URL
    # API: GET /api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=<package>&path=\LocalState&filename=asr_debug.log
    params = {
        "knownfolderid": "LocalAppData",
        "packagefullname": PACKAGE_FULL_NAME,
        "path": "\\LocalState",
        "filename": LOG_FILE_NAME
    }
    url_params = urllib.parse.urlencode(params)
    
    # We try HTTPS first, fall back to HTTP if needed
    protocols = [("https", 443), ("http", 80)]
    downloaded = False
    
    # Disable SSL verification for self-signed certificates
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    
    for proto, port in protocols:
        url = f"{proto}://{ip}:{port}/api/filesystem/apps/file?{url_params}"
        print(f"\nAttempting to connect to {proto}://{ip}:{port}...")
        
        req = urllib.request.Request(url)
        
        # Add basic authorization header
        auth_str = f"{username}:{password}"
        import base64
        encoded_auth = base64.b64encode(auth_str.encode('utf-8')).decode('utf-8')
        req.add_header("Authorization", f"Basic {encoded_auth}")
        
        try:
            with urllib.request.urlopen(req, context=ctx, timeout=10) as response:
                content = response.read()
                local_path = os.path.join(os.getcwd(), LOG_FILE_NAME)
                with open(local_path, "wb") as f:
                    f.write(content)
                print(f"Success! Log file downloaded successfully to: {local_path}")
                downloaded = True
                break
        except HTTPError as e:
            if e.code == 401:
                print("Error: Unauthorized. Please check your Device Portal username and password.")
                return
            elif e.code == 404:
                print(f"Error 404: Log file not found on the device. (Has the app run at least once to write the log?)")
                print("Also verify if the PackageFullName matches.")
                return
            else:
                print(f"HTTP Error: {e.code} - {e.reason}")
        except URLError as e:
            print(f"Connection failed: {e.reason}")
        except Exception as e:
            print(f"Unexpected error: {e}")
            
    if not downloaded:
        print("\nFailed to pull logs from the device. Please make sure:")
        print("1. The HoloLens is powered on, unlocked, and connected to the network.")
        print(f"2. You can ping the device at {ip}")
        print("3. Windows Device Portal is enabled on the device (Settings > Update & Security > For Developers).")

if __name__ == "__main__":
    main()
