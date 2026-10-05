"""Детектор характеристик железа.

Использует WMI для получения информации о CPU, RAM и дисках в Windows.
"""

import os
import ctypes
import psutil
from pathlib import Path

try:
    import win32com.client
except ImportError:
    win32com = None

class HardwareDetector:
    """Определение характеристик системы."""

    @staticmethod
    def get_total_ram_gb() -> float | None:
        """Получить общий объем RAM в ГБ."""
        try:
            return round(psutil.virtual_memory().total / 1024**3, 1)
        except (OSError, RuntimeError):
            return None  # Leave the manual RAM selection unchanged.

    @staticmethod
    def get_cpu_info() -> dict:
        """Получить информацию о CPU (ядра, гибридность)."""
        info = {
            "logical_cores": os.cpu_count() or 4,
            "physical_cores": 4,
            "is_hybrid": False,
            "p_cores": 0
        }
        
        # 1. Try WMI for physical cores
        try:
            if win32com:
                wmi = win32com.client.GetObject("winmgmts:")
                processors = wmi.ExecQuery("SELECT NumberOfCores FROM Win32_Processor")
                info["physical_cores"] = sum(int(p.NumberOfCores) for p in processors)
        except Exception:
            pass

        # 2. Try Windows API for Hybrid Core Detection (Intel 12th+)
        try:
            from ctypes import wintypes
            
            RELATIONSHIP_PROCESSOR_CORE = 0
            
            class GROUP_AFFINITY(ctypes.Structure):
                _fields_ = [
                    ("Mask", ctypes.c_size_t),
                    ("Group", wintypes.WORD),
                    ("Reserved", wintypes.WORD * 3)
                ]

            class PROCESSOR_RELATIONSHIP(ctypes.Structure):
                _fields_ = [
                    ("Flags", ctypes.c_byte),
                    ("EfficiencyClass", ctypes.c_byte),
                    ("Reserved", ctypes.c_byte * 20),
                    ("GroupCount", wintypes.WORD),
                    ("GroupMask", GROUP_AFFINITY * 1) 
                ]

            class SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX(ctypes.Structure):
                _fields_ = [
                    ("Relationship", ctypes.c_int),
                    ("Size", wintypes.DWORD),
                    ("Processor", PROCESSOR_RELATIONSHIP)
                ]

            buffer_size = wintypes.DWORD(0)
            ctypes.windll.kernel32.GetLogicalProcessorInformationEx(RELATIONSHIP_PROCESSOR_CORE, None, ctypes.byref(buffer_size))
            
            if buffer_size.value > 0:
                buffer = (ctypes.c_byte * buffer_size.value)()
                if ctypes.windll.kernel32.GetLogicalProcessorInformationEx(RELATIONSHIP_PROCESSOR_CORE, ctypes.byref(buffer), ctypes.byref(buffer_size)):
                    offset = 0
                    p_cores = 0
                    e_cores = 0
                    
                    while offset < buffer_size.value:
                        item = SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX.from_buffer(buffer, offset)
                        if item.Relationship == RELATIONSHIP_PROCESSOR_CORE:
                            if item.Processor.EfficiencyClass > 0:
                                p_cores += 1
                            else:
                                e_cores += 1
                        offset += item.Size
                    
                    if e_cores > 0 and p_cores > 0:
                        info["is_hybrid"] = True
                        info["p_cores"] = p_cores
                        info["physical_cores"] = p_cores + e_cores
                    elif e_cores > 0 or p_cores > 0:
                        info["physical_cores"] = p_cores + e_cores
        except Exception:
            if info["logical_cores"] > info["physical_cores"] * 2:
                # Potential hybrid or just SMT
                pass
            
        return info

    @staticmethod
    def get_main_disk_type(path=None) -> str:
        """Resolve the selected download volume, never pick the first physical disk."""
        import json
        import subprocess
        drive = Path(path or os.getenv("SystemDrive", "C:")).drive.rstrip(":").upper()
        if len(drive) != 1 or not drive.isalpha():
            return "Unknown"
        # Query the selected volume without admin-only Storage cmdlets.
        try:
            from ctypes import wintypes
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
            kernel.CreateFileW.restype = wintypes.HANDLE
            kernel.DeviceIoControl.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            handle = kernel.CreateFileW("\\\\.\\" + drive + ":", 0, 3, None, 3, 0, None)
            if handle != ctypes.c_void_p(-1).value:
                try:
                    def query_property(property_id):
                        query = (wintypes.DWORD * 3)(property_id, 0, 0)
                        output = ctypes.create_string_buffer(4096)
                        returned = wintypes.DWORD()
                        if not kernel.DeviceIoControl(handle, 0x2D1400, query, 12, output, 4096, ctypes.byref(returned), None):
                            return b""
                        return output.raw[:returned.value]
                    descriptor = query_property(0)
                    if len(descriptor) >= 32 and int.from_bytes(descriptor[28:32], "little") == 17:
                        return "NVMe"
                    penalty = query_property(7)
                    if len(penalty) >= 9:
                        return "HDD" if penalty[8] else "SSD"
                finally:
                    kernel.CloseHandle(handle)
        except (AttributeError, OSError):
            pass
        script = (
            "$ErrorActionPreference='Stop'; "
            f"$disk=Get-Partition -DriveLetter '{drive}' | Get-Disk; "
            "$physical=Get-PhysicalDisk | Where-Object { [string]$_.DeviceId -eq [string]$disk.Number }; "
            "[pscustomobject]@{Bus=[string]$disk.BusType;Media=[string]$physical.MediaType} | ConvertTo-Json -Compress"
        )
        try:
            result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                capture_output=True, text=True, timeout=12, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), check=True)
            data = json.loads(result.stdout)
            if data["Bus"].lower() == "nvme":
                return "NVMe"
            if data["Media"].lower() == "ssd":
                return "SSD"
            if data["Media"].lower() == "hdd":
                return "HDD"
        except (OSError, ValueError, subprocess.SubprocessError, KeyError):
            pass
        return "Unknown"

if __name__ == "__main__":
    detector = HardwareDetector()
    print(f"RAM: {detector.get_total_ram_gb()} GB")
    print(f"CPU: {detector.get_cpu_info()}")
    print(f"Disk: {detector.get_main_disk_type()}")
