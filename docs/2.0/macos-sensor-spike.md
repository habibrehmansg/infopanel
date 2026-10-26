# macOS sensor spike (Apple Silicon)

Spike date: 2026-10-26. Machine: MacBook Pro, Apple M4 Max (4 E-cores + 2×5 P-cores, 32-core GPU, 36 GB),
macOS 27.0.1 (26A434), arm64. Every probe ran as the normal user (uid 501): no sudo, no extra
entitlements, not sandboxed. Probe code (C and a .NET 10 console app) is throwaway and lives in the
session scratchpad, not in the repo.

## Verdict

**macOS can supply what users expect from an InfoPanel dashboard without root.** CPU load and
frequency, GPU load, frequency and memory, RAM, temperatures, fan RPM, system/CPU/GPU power, battery,
disk and network all came back with live values on this M4 Max. Two caveats:

1. The richest sources (AppleSMC keys, IOReport, the HID temperature sensors) are **undocumented or private
   Apple APIs**. They work today, but Apple can change them between macOS or chip releases, and key names differ by SoC generation.
2. **IOReport "Energy Model" CPU/ANE/DRAM counters are frozen for an unprivileged process on macOS 27.**
   They hold the same value for 60 s, even under 100% CPU load. That is the source macmon and asitop use for CPU power.
   The `GPU Energy` (nJ) channel still updates. Per-rail CPU power therefore has to come from SMC
   keys whose meaning we inferred, not from a documented counter. ANE power has no working source.

`powermetrics` is not an option: it exits with `powermetrics must be invoked as the superuser` (verified).

## Capability matrix

Confidence: **verified** = live value read on this machine and it behaved as expected (moved with load where
tested); **partial** = value read, but semantics inferred or not stress-tested; **unverified** = not tested here.

| Metric | Method/API | Privileges needed | Public or private API | Sample reading on this M4 Max | Confidence | .NET access route |
|---|---|---|---|---|---|---|
| CPU load total + per core | Mach `host_processor_info(PROCESSOR_CPU_LOAD_INFO)`, tick deltas | none | Public | idle 18.4% total; 14×`yes` load 99.9% | verified | P/Invoke `libSystem` |
| CPU load P vs E | per-core ticks grouped by IODeviceTree `cpus/cpuN` `cluster-type` (E: cpu0–3, P: cpu4–13); `hw.perflevel{0,1}.logicalcpu` = 10/4 | none | Public sysctl; device-tree read via public IOKit, undocumented props | E 9–26%, P 2–35% (idle) | partial: mapping assumes the Mach CPU index equals `logical-cpu-id` | P/Invoke `sysctlbyname`, IOKit registry |
| CPU frequency (per cluster / per core) | IOReport `CPU Stats` / `CPU Complex Performance States` (ECPU, PCPU, PCPU1) and `CPU Core Performance States` (per core) residency × DVFS table from IODeviceTree `pmgr` `voltage-states1-sram` (E, kHz) / `voltage-states5-sram` (P, kHz) | none | **Private** (libIOReport) + undocumented device-tree props | Under load: E 2592 MHz, P 4512 MHz (both clusters). Tables: E 1020–2592 (7 states), P 1260–4512 (20 states) | verified | P/Invoke `/usr/lib/libIOReport.dylib` (in dyld shared cache, absolute path works) + IOKit + CoreFoundation |
| CPU frequency (plain sysctl) | `hw.cpufrequency` | – | – | **absent** on Apple Silicon | verified (not available) | – |
| GPU utilisation | IOKit `IOAccelerator` (`AGXAcceleratorG16X`) property `PerformanceStatistics` → `Device Utilization %`, `Renderer Utilization %`, `Tiler Utilization %` | none | Public IOKit call; undocumented property keys | Device 34% / Renderer 34% / Tiler 17% (desktop activity), 0% idle | verified | P/Invoke IOKit `IORegistryEntryCreateCFProperty` |
| GPU frequency / active residency | IOReport `GPU Stats` / `GPU Performance States` (`GPUPH`: OFF, P1–P15) × `pmgr` `voltage-states9` (Hz): 338–1578 MHz | none | **Private** | idle: 5–6% active at P1 = 338 MHz | partial (not tested under GPU load) | P/Invoke libIOReport |
| GPU memory | `PerformanceStatistics` → `In use system memory`, `Alloc system memory` (unified memory, no VRAM) | none | Public call, undocumented keys | in use 1.09 GB, allocated 4.80 GB | verified | P/Invoke IOKit |
| Memory used/free/wired/compressed | Mach `host_statistics64(HOST_VM_INFO64)` (16 KB pages); `hw.memsize` | none | Public | free 53k pages, active 826k, wired 232k, compressor 366k pages | verified | P/Invoke libSystem |
| Memory pressure | sysctl `kern.memorystatus_vm_pressure_level` (1 normal / 2 warn / 4 critical), `kern.memorystatus_level` (% free) | none | Public sysctl (lightly documented) | level 1, 70–73% free | verified | P/Invoke `sysctlbyname` |
| Swap | sysctl `vm.swapusage` (`struct xsw_usage`) | none | Public | 2453 MB used / 4096 MB | verified | P/Invoke `sysctlbyname` |
| CPU/P-core/E-core die temps | AppleSMC user client (`IOServiceOpen("AppleSMC")`, selector 2), `flt` keys `Tp*` (P), `Te*` (E), `TCMz`/`TCMb`/`TCDX` (CPU aggregates?) | none | **Private** (SMC protocol and key names undocumented) | idle Tp1o 83 °C, Te06 69 °C, TCMz 83 °C; load TCMz 91 °C, Tp29 90.5 °C | partial: values read live and rise under load; key→sensor names inferred | P/Invoke IOKit `IOConnectCallStructMethod` (80-byte struct) **verified from .NET 10** |
| GPU die temps | SMC `Tg*` (≈44 keys) | none | **Private** | 55–62 °C idle | partial | same |
| SoC / other temps | SMC `Ts*` (SoC), `Tf*` (`Tf*6` read ~100 °C, so likely not a temperature), `TP*`/`TR*` (PMU/regulators?), `TaLP/TaRF` (airflow?), `TW0P` (wireless) | none | **Private** | Ts* 55–60 °C, TPD* 50–52 °C | partial (names unknown) | same |
| Die temps (alternative) | `IOHIDEventSystemClient` (PrimaryUsagePage 0xff00, usage 5), event type 15: `PMU tdie1..10`, `PMU tdev1..8`, `NAND CH0 temp`, `gas gauge battery` | none | **Private** (IOHIDEventSystemClient SPI) | PMU tdie 52–54 °C, NAND 36 °C, battery 31.7 °C | verified | P/Invoke IOKit (HID SPI functions) |
| GPU temp via IOReport | `GPU Stats` / `Temperature` (Tg1a… channels) | none | Private | **all zero**: unusable | verified (not available) | – |
| Battery temp | SMC `TB0T/TB1T/TB2T`; HID `gas gauge battery` | none | Private | 31.5 °C | verified | P/Invoke IOKit |
| Fan speeds | SMC `FNum` (=2), `F0Ac`/`F1Ac` actual RPM (`flt`), `F0Mn`/`F0Mx` 2317/7826 RPM | none (read only; writes need root) | **Private** | 0 RPM idle (fans stopped), 2318 RPM after load (read from .NET) | verified | P/Invoke IOKit SMC |
| GPU power | IOReport `Energy Model` / `GPU Energy` (nJ counter, delta/interval) | none | **Private** | 0.04 W idle, ~0.2 W desktop | verified | P/Invoke libIOReport (verified from .NET 10) |
| CPU / ANE / DRAM power | IOReport `Energy Model` `CPU Energy`, `EACC_CPU`, `PACC0/1_CPU`, `ANE`, `DRAM` (mJ) | none | Private | **counters frozen** (CPU Energy = 466384616 mJ across 60 s and under load) | verified (not usable unprivileged on macOS 27) | – |
| CPU power (alternative) | SMC `PHPC` (flt, W): 8.7 W idle → 30.0 W at 14-core load; `PHPS` 8.2 → 22.3 W tracks too | none | **Private**, key meaning inferred | see left | partial | P/Invoke IOKit SMC |
| System / package power | SMC `PSTR` (system total, W), `PDTR` (DC-in, W: 17.4 → 42.1 W under load), `PPBR` (battery rail) | none | **Private** | PSTR 13.7–25 W, PDTR 17–42 W | partial (PSTR looked lagged/averaged) | P/Invoke IOKit SMC |
| System power in (alternative) | IOKit `AppleSmartBattery` → `PowerTelemetryData` {`SystemPowerIn`, `SystemLoad`, `BatteryPower`, `AdapterPower`} (mW), `AdapterDetails.Watts` | none | Public call, undocumented keys | SystemPowerIn 33.9 W, adapter 100 W | verified | P/Invoke IOKit registry |
| Battery stats | `AppleSmartBattery` (`CurrentCapacity`, `MaxCapacity`, `CycleCount`, `Voltage`, `Amperage`, `IsCharging`, `ExternalConnected`, `TimeRemaining`); public `IOPSCopyPowerSourcesInfo` (what `pmset -g batt` uses) | none | ioreg keys undocumented; IOPowerSources **public** | 100%, 80 cycles, 13.058 V, on AC, charged | verified (registry); IOPS API itself untested, `pmset` output verified | P/Invoke IOKit / IOPowerSources |
| Thermal throttling state | `pmset -g therm`; `NSProcessInfo.thermalState` | none | Public | "No thermal warning level has been recorded" | partial (NSProcessInfo untested) | spawn `pmset`, or ObjC runtime P/Invoke |
| Disk throughput | IOKit `IOBlockStorageDriver` `Statistics` (`Bytes (Read)`/`Bytes (Write)` deltas); `iostat` | none | **Public** | 16.0 MB/s read, 22.2 MB/s write (sample) | verified | P/Invoke IOKit, or spawn `iostat` |
| Network throughput | sysctl `NET_RT_IFLIST2` → `if_msghdr2.ifm_data` byte counters (64-bit); `netstat -ib` | none | **Public** | en0 rx 25.6 KB/s, tx 14.3 KB/s | verified | P/Invoke sysctl; .NET `NetworkInterface.GetIPStatistics()` likely works too (unverified) |
| Anything via `powermetrics` | `powermetrics --samplers …` | **root** | Public tool | "must be invoked as the superuser" | verified (blocked) | spawn as root only: rejected |

SMC byte order on Apple Silicon: `flt` keys are native little-endian floats; `ui16` battery keys (e.g. `B0AV` = 819) only make sense
byte-swapped (0x3303 = 13059 mV, matching ioreg 13058). Decode per type carefully.

## Recommended macOS ISensorSource v1

Ship in this order. Everything runs in-process through P/Invoke, with no helper and no root. The .NET 10 probe in the scratchpad
already calls IOReport, CoreFoundation and the SMC user client successfully.

1. **Public baseline (zero platform risk).** CPU load total/per core and P/E split (Mach + sysctl + device-tree cluster map),
   memory used/free/wired/compressed/pressure/swap, disk and network throughput, battery % / cycles / voltage / charging.
2. **Undocumented-but-public-call IOKit properties.** GPU utilisation and GPU memory from `IOAccelerator.PerformanceStatistics`,
   system power-in from `AppleSmartBattery.PowerTelemetryData`. Isolate each behind a key lookup that degrades to "sensor missing".
3. **SMC reader (private, highest value).** Enumerate all keys at start-up (`#KEY` count, then index reads, about 3.5k keys)
   and expose every `T*` key that reads a plausible temperature as a sensor, plus `F*Ac` fans, `PSTR`/`PDTR`/`PHPC`. Raw key IDs are the sensor IDs.
   Ship a small per-SoC alias table ("CPU P-core max", "GPU avg") computed as max/avg over the `Tp*`/`Te*`/`Tg*` families,
   not hard-coded single keys. This mirrors the "HWiNFO sensor list" model users already know.
4. **IOReport (private).** CPU cluster/core frequency (residency × `pmgr` DVFS tables), GPU frequency/active residency,
   GPU power (`GPU Energy`). Keep one long-lived subscription and sample deltas on the engine tick.
5. **Later / optional.** HID `PMU tdie*` temps (redundant with SMC), `NSProcessInfo.thermalState`.

Do not build on `powermetrics` (root), IOReport CPU/ANE/DRAM energy (frozen), or IOReport GPU `Temperature` (zeros).

## Gaps / risks

- **Private API churn.** SMC, IOReport and IOHIDEventSystemClient are undocumented. Key names already differ between Apple
  chip generations (unverified beyond this M4 Max; M1–M3 sensor key lists differ in public tools). DVFS table units also look
  generation-specific: CPU tables here are kHz and the GPU table is Hz (verified here; behaviour on other chips unverified). Mitigation: enumerate
  dynamically, sanity-range values, and never crash on a missing key.
- **CPU/ANE power regression.** The frozen IOReport Energy Model mJ counters are the main finding. Whether this is a macOS 27
  change, a missing entitlement, or a subscription quirk is **unknown**. CPU power via SMC `PHPC` is an inference from load
  behaviour only. ANE power: no unprivileged source found.
- **Sensor labelling.** About 350 `T*` SMC keys exist, but Apple publishes no names. Friendly names ("P-core 3") need a
  community-maintained map per SoC. Some keys are not temperatures (`Tf*6` ≈ 100 °C, `Ta0*` = 8.0).
- **Distribution.** Private-API use means **no Mac App Store** (App Review rejects it, and the App Sandbox very likely blocks
  `IOServiceOpen(AppleSMC)`: unverified). Developer-ID signing plus notarisation is the route. Notarisation is a malware
  scan and does not reject private symbols, the route tools like Stats take (not verified for our binary). Hardened runtime needs
  no extra entitlement for these calls, as far as tested (the probe ran unsigned under `dotnet`).
- **Untested here:** GPU frequency under real GPU load, the `IOPSCopyPowerSourcesInfo` call itself, Intel Macs (out of scope;
  SMC keys differ), other M-series chips, sandboxed execution.
- **Fan control** (writing `F0Md`/`F0Tg`) needs root and is out of scope; reads are fine.
