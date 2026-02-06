"""Детектор характеристик железа.

Использует WMI для получения информации о CPU, RAM и дисках в Windows.
"""

import os
import ctypes
from pathlib import Path

try:
    import win32com.client
except ImportError:
    win32com = None

class HardwareDetector:
    """Определение характеристик системы."""

    @staticmethod
    def get_total_ram_gb() -> float:
        """Получить общий объем RAM в ГБ."""
        try:
            if win32com:
                wmi = win32com.client.GetObject("winmgmts:")
                mem = wmi.ExecQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")[0]
                return round(int(mem.TotalPhysicalMemory) / (1024**3), 1)
        except Exception as e:
            print(f"Error detecting RAM via WMI: {e}")
            
        # Fallback via ctypes (Windows)
        try:
            class MEMORYSTATUSEX(ctypes.Structure):
                _fields_ = [
                    ("dwLength", ctypes.c_ulong),
                    ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong),
                    ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("sullAvailExtendedVirtual", ctypes.c_ulonglong),
                ]
            
            stat = MEMORYSTATUSEX()
            stat.dwLength = ctypes.sizeof(stat)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))
            return round(stat.ullTotalPhys / (1024**3), 1)
        except Exception:
            pass
            
        return 8.0  # Default fallback

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
    def get_main_disk_type() -> str:
        """Определить тип основного накопителя (HDD, SSD, NVMe)."""
        try:
            if win32com:
                wmi = win32com.client.GetObject("winmgmts:")
                system_drive = os.getenv("SystemDrive", "C:")
                
                # Check MSFT_PhysicalDisk first as it is more reliable for MediaType
                try:
                    storage_wmi = win32com.client.GetObject("winmgmts:\\\\.\\root\\Microsoft\\Windows\\Storage")
                    phys_disks = storage_wmi.ExecQuery("SELECT DeviceID, Model, MediaType, Bustype FROM MSFT_PhysicalDisk")
                    for d in phys_disks:
                        # BusType 17 is NVMe
                        if getattr(d, 'BusType', 0) == 17 or "NVME" in d.Model.upper():
                            return "NVMe"
                        if d.MediaType == 4: # SSD
                            return "SSD"
                except Exception:
                    pass

                # Fallback to Win32_DiskDrive mapping
                partitions = wmi.ExecQuery("SELECT * FROM Win32_LogicalDiskToPartition")
                for p in partitions:
                    if system_drive in p.Dependent:
                        part_id = p.Antecedent.split('"')[1]
                        drives = wmi.ExecQuery("SELECT * FROM Win32_DiskDriveToDiskPartition")
                        for d in drives:
                            if part_id in d.Dependent:
                                drive_id = d.Antecedent.split('"')[1]
                                escaped_id = drive_id.replace('\\', '\\\\')
                                disk = wmi.ExecQuery(f"SELECT Model, InterfaceType FROM Win32_DiskDrive WHERE DeviceID = '{escaped_id}'")[0]
                                model = disk.Model.upper()
                                interface = disk.InterfaceType.upper()
                                
                                if "NVME" in model or "NVME" in interface:
                                    return "NVMe"
                                if "SSD" in model:
                                    return "SSD"
        except Exception:
            pass
            
        return "HDD"

if __name__ == "__main__":
    detector = HardwareDetector()
    print(f"RAM: {detector.get_total_ram_gb()} GB")
    print(f"CPU: {detector.get_cpu_info()}")
    print(f"Disk: {detector.get_main_disk_type()}")
