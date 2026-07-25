#!/usr/bin/env python3
import asyncio
import socket
import time
from statistics import median
from bleak import BleakScanner

MASTER_IP = "192.168.50.1"
MASTER_PORT = 5005

TARGET_NAME = "ESP32_BEACON_X"
TARGET_MAC = "34:85:18:7B:DA:ED"   # set to None if MAC changes
WINDOW_SEC = 2.0

samples = []


def matches_beacon(device, adv):
    if TARGET_MAC is not None:
        if (device.address or "").lower() != TARGET_MAC.lower():
            return False
    return device.name == TARGET_NAME


def cb(device, adv):
    if not matches_beacon(device, adv):
        return
    rssi = adv.rssi
    if rssi is None:
        return
    samples.append((time.time(), float(rssi)))


async def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    scanner = BleakScanner(cb)
    await scanner.start()

    print(f"[SLAVE] scanning... sending to {MASTER_IP}:{MASTER_PORT}")
    print(f"        TARGET_NAME={TARGET_NAME}")
    print(f"        TARGET_MAC={TARGET_MAC}")

    try:
        last_send = 0.0

        while True:
            now = time.time()
            cutoff = now - WINDOW_SEC

            while samples and samples[0][0] < cutoff:
                samples.pop(0)

            if (now - last_send) >= WINDOW_SEC and samples:
                med = median([r for _, r in samples])
                msg = f"B,{now:.3f},{med:.2f}"
                sock.sendto(msg.encode(), (MASTER_IP, MASTER_PORT))
                print(f"[SLAVE] median RSSI={med:.1f} dBm sent")
                last_send = now

            await asyncio.sleep(0.05)

    finally:
        await scanner.stop()


if __name__ == "__main__":
    asyncio.run(main())