#!/usr/bin/env python3
import asyncio
import socket
import time
import math
from statistics import median
from bleak import BleakScanner

# =========================
# NETWORK
# =========================
LISTEN_IP = "0.0.0.0"
PORT = 5005
FRESH_SEC = 5.0

# =========================
# GEOMETRY
# =========================
BASELINE_D = 1.0   # distance between Pi A and Pi B in meters

# =========================
# TARGET
# =========================
TARGET_NAME = "ESP32_BEACON_X"
TARGET_MAC = "34:85:18:7B:DA:ED"   # set to None if MAC changes

# =========================
# CALIBRATION
# =========================
# From your earlier 0.5m and 1m measurements
RSSI_1M_A = -55.0
N_A = 3.32

RSSI_1M_B = -57.0
N_B = 2.33

# =========================
# FILTERING / STABILITY
# =========================
WINDOW_SEC = 2.0          # median window for master Pi local RSSI
EMA_ALPHA = 0.25          # smoothing for computed distances
MIN_DIST = 0.20           # do not trust distances smaller than this
MAX_DIST = 6.00           # cap huge nonsense values
GEOM_EPS = 0.02           # small tolerance for geometry fixing

samplesA = []             # list of (timestamp, rssi)
lastB = None              # (timestamp, rssi)

smooth_rA = None
smooth_rB = None


def matches_beacon(device, adv):
    if TARGET_MAC is not None:
        if (device.address or "").lower() != TARGET_MAC.lower():
            return False
    return device.name == TARGET_NAME


def ema(prev, new, alpha):
    if prev is None:
        return new
    return alpha * new + (1.0 - alpha) * prev


def rssi_to_distance(rssi, rssi_1m, n):
    d = 10 ** ((rssi_1m - rssi) / (10.0 * n))
    d = max(MIN_DIST, min(MAX_DIST, d))
    return d


def fix_geometry(rA, rB, baseline):
    """
    Make the circles solvable even when RSSI estimates are physically inconsistent.

    Cases:
    1) Too far apart: rA + rB < baseline
       -> scale them up so circles just touch.

    2) One circle completely inside the other:
       abs(rA - rB) > baseline
       -> shrink the larger difference so circles just touch internally.
    """
    # External no-intersection
    if rA + rB < baseline:
        needed = baseline + GEOM_EPS
        scale = needed / max(rA + rB, 1e-9)
        rA *= scale
        rB *= scale

    # Internal no-intersection
    diff = abs(rA - rB)
    if diff > baseline:
        target_diff = max(baseline - GEOM_EPS, 0.0)
        if rA > rB:
            rA = rB + target_diff
        else:
            rB = rA + target_diff

    return rA, rB


def circle_intersections(rA, rB, d):
    """
    Returns two solutions: (x,+y), (x,-y)
    with Pi A at (0,0) and Pi B at (d,0)
    """
    x = (rA * rA - rB * rB + d * d) / (2.0 * d)
    y2 = rA * rA - x * x

    # Because of floating point or near-touching geometry
    if y2 < 0 and y2 > -1e-6:
        y2 = 0.0

    if y2 < 0:
        return None

    y = math.sqrt(y2)
    return (x, y), (x, -y)


def angle_deg(x, y):
    return math.degrees(math.atan2(y, x))


def cb_A(device, adv):
    if not matches_beacon(device, adv):
        return
    rssi = adv.rssi
    if rssi is None:
        return
    samplesA.append((time.time(), float(rssi)))


async def udp_listener():
    global lastB

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((LISTEN_IP, PORT))
    sock.setblocking(False)

    print(f"[MASTER] Listening UDP on {LISTEN_IP}:{PORT}")

    loop = asyncio.get_running_loop()

    while True:
        data, _ = await loop.sock_recvfrom(sock, 1024)
        msg = data.decode(errors="ignore").strip()

        try:
            pid, ts, rssi = msg.split(",")
            if pid.strip() == "B":
                lastB = (time.time(), float(rssi))
        except Exception:
            pass


async def solver_loop():
    global smooth_rA, smooth_rB

    print("[MASTER] scanning locally + using slave RSSI")
    print(f"         TARGET_NAME={TARGET_NAME}")
    print(f"         TARGET_MAC={TARGET_MAC}")
    print(f"         BASELINE_D={BASELINE_D:.2f} m")

    while True:
        now = time.time()

        cutoff = now - WINDOW_SEC
        while samplesA and samplesA[0][0] < cutoff:
            samplesA.pop(0)

        if not samplesA or not lastB:
            await asyncio.sleep(0.1)
            continue

        if now - lastB[0] > FRESH_SEC:
            await asyncio.sleep(0.1)
            continue

        rssiA = median([r for _, r in samplesA])
        rssiB = lastB[1]

        raw_rA = rssi_to_distance(rssiA, RSSI_1M_A, N_A)
        raw_rB = rssi_to_distance(rssiB, RSSI_1M_B, N_B)

        smooth_rA = ema(smooth_rA, raw_rA, EMA_ALPHA)
        smooth_rB = ema(smooth_rB, raw_rB, EMA_ALPHA)

        rA = smooth_rA
        rB = smooth_rB

        closer = "A" if rA < rB else "B"
        raw_status = "OK"

        # Fix impossible geometry instead of failing
        pre_sum = rA + rB
        pre_diff = abs(rA - rB)

        if pre_sum < BASELINE_D or pre_diff > BASELINE_D:
            raw_status = "ADJUSTED"
            rA, rB = fix_geometry(rA, rB, BASELINE_D)

        sols = circle_intersections(rA, rB, BASELINE_D)
        if sols is None:
            print(
                f"RSSI A={rssiA:6.1f} B={rssiB:6.1f} | "
                f"raw rA={raw_rA:.2f} raw rB={raw_rB:.2f} | "
                f"smoothed rA={smooth_rA:.2f} smoothed rB={smooth_rB:.2f} | "
                f"closer={closer} | still unsolved"
            )
            await asyncio.sleep(0.2)
            continue

        (x1, y1), (x2, y2) = sols
        th1 = angle_deg(x1, y1)
        th2 = angle_deg(x2, y2)

        print(
            f"RSSI A={rssiA:6.1f}dBm B={rssiB:6.1f}dBm | "
            f"raw rA={raw_rA:4.2f}m raw rB={raw_rB:4.2f}m | "
            f"use rA={rA:4.2f}m use rB={rB:4.2f}m | "
            f"{raw_status} | closer={closer} | "
            f"pos≈({x1:4.2f},{y1:4.2f}) or ({x2:4.2f},{y2:4.2f}) | "
            f"θA≈{th1:5.1f}° or {th2:5.1f}°"
        )

        await asyncio.sleep(0.2)


async def main():
    scanner = BleakScanner(cb_A)
    await scanner.start()
    try:
        await asyncio.gather(udp_listener(), solver_loop())
    finally:
        await scanner.stop()


if __name__ == "__main__":
    asyncio.run(main())