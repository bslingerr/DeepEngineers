"""Python (ctypes, stdlib only) access to the DeepEngineers section and the Win32 calls the tools need.

Shared by tests/standins/*, tools/spy/de_spy.py and src/DeepEngineers.Launcher/launcher.py. Independent of the C#
code: the layout comes from tests/wire/layout_py.py (written by hand from CONTRACT), the behaviour from CONTRACT §5.3
(seqlock, rings), §5.2 (re-sync) and §6 (security descriptor, creator/opener rules).

Rules kept here (CONTRACT §6):
- only `Section.create` creates (CreateFileMappingW with the CONTRACT security descriptor built by
 ConvertStringSecurityDescriptorToSecurityDescriptorW); it is used by tests/standins/fake_sn_host.py only;
- every other tool uses `Section.open` (OpenFileMappingW, open only), the spy with FILE_MAP_READ;
- no `mmap` tag names (they create the section with default security).
Every memory access is bounds-checked: a stray pointer in ctypes would crash the interpreter.
"""
from __future__ import annotations

import ctypes
import hashlib
import os
import struct
import sys
import time
from ctypes import wintypes as wt

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import layout_py as L  # noqa: E402

if os.name != "nt":
    raise ImportError("section_py needs Windows (kernel32 file mappings)")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
adv = ctypes.WinDLL("advapi32", use_last_error=True)
usr = ctypes.WinDLL("user32", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")
try:
    winmm = ctypes.WinDLL("winmm")
except OSError:  # pragma: no cover
    winmm = None

HANDLE = wt.HANDLE
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

PAGE_READWRITE = 0x04
FILE_MAP_WRITE = 0x0002
FILE_MAP_READ = 0x0004
SECTION_QUERY = 0x0001
ERROR_FILE_NOT_FOUND = 2
ERROR_ACCESS_DENIED = 5
ERROR_INVALID_HANDLE = 6
ERROR_INVALID_PARAMETER = 87
ERROR_ALREADY_EXISTS = 183
SYNCHRONIZE = 0x00100000
PROCESS_TERMINATE = 0x0001
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TOKEN_QUERY = 0x0008
WAIT_OBJECT_0 = 0x00000000
WAIT_ABANDONED = 0x00000080
WAIT_TIMEOUT = 0x00000102
WAIT_FAILED = 0xFFFFFFFF
STILL_ACTIVE = 259
SDDL_REVISION_1 = 1
SE_KERNEL_OBJECT = 6
DACL_SECURITY_INFORMATION = 0x4
LABEL_SECURITY_INFORMATION = 0x10
MASK32 = 0xFFFFFFFF
MASK64 = 0xFFFFFFFFFFFFFFFF

class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("nLength", wt.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p), ("bInheritHandle", wt.BOOL)]

class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]

class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", wt.DWORD), ("dwMemoryLoad", wt.DWORD), ("ullTotalPhys", ctypes.c_uint64),
                ("ullAvailPhys", ctypes.c_uint64), ("ullTotalPageFile", ctypes.c_uint64),
                ("ullAvailPageFile", ctypes.c_uint64), ("ullTotalVirtual", ctypes.c_uint64),
                ("ullAvailVirtual", ctypes.c_uint64), ("ullAvailExtendedVirtual", ctypes.c_uint64)]

class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", wt.LONG), ("dwFlags", wt.DWORD),
                ("szExeFile", wt.WCHAR * 260)]

def _proto(fn, restype, *argtypes):
    fn.restype = restype
    fn.argtypes = list(argtypes)
    return fn

_CreateFileMappingW = _proto(k32.CreateFileMappingW, HANDLE, HANDLE, ctypes.POINTER(SECURITY_ATTRIBUTES), wt.DWORD,
                             wt.DWORD, wt.DWORD, wt.LPCWSTR)
_OpenFileMappingW = _proto(k32.OpenFileMappingW, HANDLE, wt.DWORD, wt.BOOL, wt.LPCWSTR)
_MapViewOfFile = _proto(k32.MapViewOfFile, ctypes.c_void_p, HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t)
_UnmapViewOfFile = _proto(k32.UnmapViewOfFile, wt.BOOL, ctypes.c_void_p)
_VirtualQuery = _proto(k32.VirtualQuery, ctypes.c_size_t, ctypes.c_void_p, ctypes.POINTER(MEMORY_BASIC_INFORMATION),
                       ctypes.c_size_t)
_CloseHandle = _proto(k32.CloseHandle, wt.BOOL, HANDLE)
_CreateMutexW = _proto(k32.CreateMutexW, HANDLE, ctypes.c_void_p, wt.BOOL, wt.LPCWSTR)
_OpenMutexW = _proto(k32.OpenMutexW, HANDLE, wt.DWORD, wt.BOOL, wt.LPCWSTR)
_ReleaseMutex = _proto(k32.ReleaseMutex, wt.BOOL, HANDLE)
_WaitForSingleObject = _proto(k32.WaitForSingleObject, wt.DWORD, HANDLE, wt.DWORD)
_OpenProcess = _proto(k32.OpenProcess, HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
_GetExitCodeProcess = _proto(k32.GetExitCodeProcess, wt.BOOL, HANDLE, ctypes.POINTER(wt.DWORD))
_TerminateProcess = _proto(k32.TerminateProcess, wt.BOOL, HANDLE, wt.UINT)
_GetTickCount64 = _proto(k32.GetTickCount64, ctypes.c_uint64)
_QueryPerformanceCounter = _proto(k32.QueryPerformanceCounter, wt.BOOL, ctypes.POINTER(ctypes.c_int64))
_QueryPerformanceFrequency = _proto(k32.QueryPerformanceFrequency, wt.BOOL, ctypes.POINTER(ctypes.c_int64))
_GetCurrentProcessId = _proto(k32.GetCurrentProcessId, wt.DWORD)
_GetCurrentProcess = _proto(k32.GetCurrentProcess, HANDLE)
_LocalFree = _proto(k32.LocalFree, ctypes.c_void_p, ctypes.c_void_p)
_GlobalMemoryStatusEx = _proto(k32.GlobalMemoryStatusEx, wt.BOOL, ctypes.POINTER(MEMORYSTATUSEX))
_CreateToolhelp32Snapshot = _proto(k32.CreateToolhelp32Snapshot, HANDLE, wt.DWORD, wt.DWORD)
_Process32FirstW = _proto(k32.Process32FirstW, wt.BOOL, HANDLE, ctypes.POINTER(PROCESSENTRY32W))
_Process32NextW = _proto(k32.Process32NextW, wt.BOOL, HANDLE, ctypes.POINTER(PROCESSENTRY32W))
_QueryFullProcessImageNameW = _proto(k32.QueryFullProcessImageNameW, wt.BOOL, HANDLE, wt.DWORD, wt.LPWSTR,
                                     ctypes.POINTER(wt.DWORD))

_ConvertStringSDToSD = _proto(adv.ConvertStringSecurityDescriptorToSecurityDescriptorW, wt.BOOL, wt.LPCWSTR,
                              wt.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wt.ULONG))
_ConvertSDToStringSD = _proto(adv.ConvertSecurityDescriptorToStringSecurityDescriptorW, wt.BOOL, ctypes.c_void_p,
                              wt.DWORD, wt.DWORD, ctypes.POINTER(wt.LPWSTR), ctypes.POINTER(wt.ULONG))
_GetSecurityInfo = _proto(adv.GetSecurityInfo, wt.DWORD, HANDLE, ctypes.c_int, wt.DWORD, ctypes.c_void_p,
                          ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))
_OpenProcessToken = _proto(adv.OpenProcessToken, wt.BOOL, HANDLE, wt.DWORD, ctypes.POINTER(HANDLE))
_GetTokenInformation = _proto(adv.GetTokenInformation, wt.BOOL, HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD,
                              ctypes.POINTER(wt.DWORD))
_ConvertSidToStringSidW = _proto(adv.ConvertSidToStringSidW, wt.BOOL, ctypes.c_void_p, ctypes.POINTER(wt.LPWSTR))
_GetSidSubAuthorityCount = _proto(adv.GetSidSubAuthorityCount, ctypes.POINTER(ctypes.c_ubyte), ctypes.c_void_p)
_GetSidSubAuthority = _proto(adv.GetSidSubAuthority, ctypes.POINTER(wt.DWORD), ctypes.c_void_p, wt.DWORD)

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
_EnumWindows = _proto(usr.EnumWindows, wt.BOOL, WNDENUMPROC, wt.LPARAM)
_GetWindowThreadProcessId = _proto(usr.GetWindowThreadProcessId, wt.DWORD, wt.HWND, ctypes.POINTER(wt.DWORD))
_IsWindowVisible = _proto(usr.IsWindowVisible, wt.BOOL, wt.HWND)
_IsIconic = _proto(usr.IsIconic, wt.BOOL, wt.HWND)
_GetWindow = _proto(usr.GetWindow, wt.HWND, wt.HWND, wt.UINT)
_PostMessageW = _proto(usr.PostMessageW, wt.BOOL, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
_AllowSetForegroundWindow = _proto(usr.AllowSetForegroundWindow, wt.BOOL, wt.DWORD)
_GetForegroundWindow = _proto(usr.GetForegroundWindow, wt.HWND)
_GetWindowTextW = _proto(usr.GetWindowTextW, ctypes.c_int, wt.HWND, wt.LPWSTR, ctypes.c_int)

_NtQuerySystemInformation = _proto(ntdll.NtQuerySystemInformation, ctypes.c_long, ctypes.c_int, ctypes.c_void_p,
                                   wt.ULONG, ctypes.POINTER(wt.ULONG))
_VirtualAlloc = _proto(k32.VirtualAlloc, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD)
_VirtualProtect = _proto(k32.VirtualProtect, wt.BOOL, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, ctypes.POINTER(wt.DWORD))
_FlushInstructionCache = _proto(k32.FlushInstructionCache, wt.BOOL, HANDLE, ctypes.c_void_p, ctypes.c_size_t)

# ----------------------------------------------------------------------------------------------------------------
# Atomic compare-and-swap on a u32 of the section.
# kernel32 exports no InterlockedCompareExchange on x64 (it is a compiler intrinsic), so the eight bytes of
# `lock cmpxchg` are placed in a private page and called through ctypes:
# mov eax, r8d; lock cmpxchg dword ptr [rcx], edx; ret
# i.e. uint32 cas(uint32* p, uint32 new, uint32 expected) -> the value found (x64 calling convention).
# ----------------------------------------------------------------------------------------------------------------

_CAS_CODE = bytes((0x44, 0x89, 0xC0, 0xF0, 0x0F, 0xB1, 0x11, 0xC3))
_MEM_COMMIT_RESERVE = 0x3000
_PAGE_EXECUTE_READ = 0x20
_cas_fn = None

def _cas32():
    global _cas_fn
    if _cas_fn is None:
        if ctypes.sizeof(ctypes.c_void_p) != 8:
            raise OSError("section_py needs a 64-bit Python (x64, like both games)")
        mem = _VirtualAlloc(None, 4096, _MEM_COMMIT_RESERVE, PAGE_READWRITE)
        if not mem:
            raise win_error(last_error(), "VirtualAlloc")
        ctypes.memmove(mem, _CAS_CODE, len(_CAS_CODE))
        old = wt.DWORD()
        if not _VirtualProtect(mem, 4096, _PAGE_EXECUTE_READ, ctypes.byref(old)):
            raise win_error(last_error(), "VirtualProtect")
        _FlushInstructionCache(_GetCurrentProcess(), mem, len(_CAS_CODE))
        _cas_fn = ctypes.CFUNCTYPE(ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32)(mem)
    return _cas_fn

# ----------------------------------------------------------------------------------------------------------------
# Clocks, process id, timer resolution
# ----------------------------------------------------------------------------------------------------------------

def tick64() -> int:
    """GetTickCount64 (ms): the heartbeat clock of CONTRACT/ARCHITECTURE §5.14."""
    return int(_GetTickCount64())

_QPF = ctypes.c_int64()
_QueryPerformanceFrequency(ctypes.byref(_QPF))
QPC_FREQUENCY = int(_QPF.value)

def qpc() -> int:
    v = ctypes.c_int64()
    _QueryPerformanceCounter(ctypes.byref(v))
    return int(v.value)

def now_s() -> float:
    """QPC seconds: one clock for every process on this machine (event timestamps across processes)."""
    return qpc() / QPC_FREQUENCY

def current_pid() -> int:
    return int(_GetCurrentProcessId())

class _PowerThrottlingState(ctypes.Structure):
    _fields_ = [("Version", ctypes.c_ulong), ("ControlMask", ctypes.c_ulong), ("StateMask", ctypes.c_ulong)]

def _power_throttling_opt_out():
    """SetProcessInformation(ProcessPowerThrottling) with EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION controlled and off
 (0x5, as the plugins' DE.Perf opt-out). S5 integration: Windows 11 stops honouring timeBeginPeriod for a
 windowless stand-in about 10 s after its start while the session is idle (sleep(1 ms) became 15.3 ms, the SE
 stand-in ticked at 15.6/31.2 ms and the S1 dry run's S1.N1/S1.M2 rows went red); the opt-out keeps the 1 ms timer."""
    try:
        st = _PowerThrottlingState(1, 0x1 | 0x4, 0)
        ctypes.WinDLL("kernel32").SetProcessInformation(ctypes.c_void_p(-1), 4, ctypes.byref(st), ctypes.sizeof(st))
    except Exception:  # pragma: no cover
        pass

def fine_timer(enable: bool = True):
    """timeBeginPeriod(1) so that time.sleep has ~1 ms granularity (stand-in frame loops), with the power-throttling
 opt-out that keeps it honoured in a windowless background process."""
    if winmm is None:
        return
    try:
        if enable:
            _power_throttling_opt_out()
        (winmm.timeBeginPeriod if enable else winmm.timeEndPeriod)(1)
    except Exception:  # pragma: no cover
        pass

def last_error() -> int:
    return ctypes.get_last_error()

def win_error(err: int, what: str) -> OSError:
    return OSError(err, "%s failed: %s (Win32 %d)" % (what, ctypes.FormatError(err).strip(), err))

# ----------------------------------------------------------------------------------------------------------------
# Security: current user SID, SDDL -> security descriptor, a handle's SDDL
# ----------------------------------------------------------------------------------------------------------------

def _token_info(htoken, cls: int) -> ctypes.Array:
    n = wt.DWORD(0)
    _GetTokenInformation(htoken, cls, None, 0, ctypes.byref(n))
    if n.value == 0:
        raise win_error(last_error(), "GetTokenInformation(size)")
    buf = ctypes.create_string_buffer(n.value)
    if not _GetTokenInformation(htoken, cls, buf, n, ctypes.byref(n)):
        raise win_error(last_error(), "GetTokenInformation")
    return buf

def _sid_string(psid) -> str:
    s = wt.LPWSTR()
    if not _ConvertSidToStringSidW(psid, ctypes.byref(s)):
        raise win_error(last_error(), "ConvertSidToStringSidW")
    try:
        return s.value
    finally:
        _LocalFree(s)

def current_user_sid() -> str:
    """The SID of the user of this process token (TokenUser), as S-1-5-..."""
    h = HANDLE()
    if not _OpenProcessToken(_GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(h)):
        raise win_error(last_error(), "OpenProcessToken")
    try:
        buf = _token_info(h, 1)  # TokenUser: TOKEN_USER { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
        psid = ctypes.c_void_p.from_buffer(buf, 0).value
        return _sid_string(psid)
    finally:
        _CloseHandle(h)

def section_sddl_for_current_user() -> str:
    return L.sddl_for(current_user_sid())

def security_descriptor_from_sddl(sddl: str) -> ctypes.c_void_p:
    sd = ctypes.c_void_p()
    if not _ConvertStringSDToSD(sddl, SDDL_REVISION_1, ctypes.byref(sd), None):
        raise win_error(last_error(), "ConvertStringSecurityDescriptorToSecurityDescriptorW")
    return sd

def handle_sddl(handle) -> str:
    """DACL + mandatory label of a kernel object handle, as SDDL (needs READ_CONTROL on the handle)."""
    psd = ctypes.c_void_p()
    info = DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION
    err = _GetSecurityInfo(handle, SE_KERNEL_OBJECT, info, None, None, None, None, ctypes.byref(psd))
    if err != 0:
        raise win_error(err, "GetSecurityInfo")
    try:
        s = wt.LPWSTR()
        if not _ConvertSDToStringSD(psd, SDDL_REVISION_1, info, ctypes.byref(s), None):
            raise win_error(last_error(), "ConvertSecurityDescriptorToStringSecurityDescriptorW")
        try:
            return s.value
        finally:
            _LocalFree(s)
    finally:
        _LocalFree(psd)

# ----------------------------------------------------------------------------------------------------------------
# The section
# ----------------------------------------------------------------------------------------------------------------

class Section:
    """A mapped view of a named, page-file-backed section. Not thread-safe; one project thread."""

    def __init__(self, name, handle, base, mapped, existed, readonly, requested):
        self.name = name
        self.handle = handle
        self.base = base
        self.mapped_size = mapped
        self.existed = existed
        self.readonly = readonly
        self.requested_size = requested

    # -- construction ---------------------------------------------------------------------------------------------
    @staticmethod
    def full_name(name: str) -> str:
        return name if "\\" in name else "Local\\" + name

    @classmethod
    def create(cls, name: str, size: int, sddl: str):
        """CreateFileMappingW with the security descriptor (never default security). Returns (section, existed).
 A new section is mapped at `size` (zero-filled by the OS); an existing one is mapped whole, whatever its size,
 so the creator rules can read it and refuse without writing. Raises OSError."""
        if not sddl:
            raise ValueError("a security descriptor is required (CONTRACT 6)")
        full = cls.full_name(name)
        sd = security_descriptor_from_sddl(sddl)
        try:
            sa = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), sd, 0)
            h = _CreateFileMappingW(INVALID_HANDLE_VALUE, ctypes.byref(sa), PAGE_READWRITE, (size >> 32) & MASK32,
                                    size & MASK32, full)
            err = last_error()
        finally:
            _LocalFree(sd)
        if not h:
            raise win_error(err, "CreateFileMappingW(%s)" % full)
        existed = err == ERROR_ALREADY_EXISTS
        view = _MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0 if existed else size)
        if not view:
            err = last_error()
            _CloseHandle(h)
            raise win_error(err, "MapViewOfFile(%s)" % full)
        mapped = _view_size(view, 0 if existed else size)
        return cls(full, h, view, mapped, existed, False, size), existed

    @classmethod
    def open(cls, name: str, readonly: bool = False):
        """OpenFileMappingW, open only. None when the section does not exist (ERROR_FILE_NOT_FOUND)."""
        full = cls.full_name(name)
        access = FILE_MAP_READ if readonly else (FILE_MAP_READ | FILE_MAP_WRITE)
        h = _OpenFileMappingW(access, False, full)
        if not h:
            err = last_error()
            if err == ERROR_FILE_NOT_FOUND:
                return None
            raise win_error(err, "OpenFileMappingW(%s)" % full)
        view = _MapViewOfFile(h, access, 0, 0, 0)
        if not view:
            err = last_error()
            _CloseHandle(h)
            raise win_error(err, "MapViewOfFile(%s)" % full)
        return cls(full, h, view, _view_size(view, 0), True, readonly, 0)

    def close(self):
        v, h = self.base, self.handle
        self.base, self.handle = None, None
        if v:
            _UnmapViewOfFile(v)
        if h:
            _CloseHandle(h)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    @property
    def is_open(self) -> bool:
        return bool(self.base)

    # -- raw memory (bounds-checked) ------------------------------------------------------------------------------
    def _chk(self, off: int, n: int, write: bool = False):
        if not self.base:
            raise ValueError("section closed")
        if off < 0 or n < 0 or off + n > self.mapped_size:
            raise IndexError("access 0x%x+%d outside the mapped 0x%x bytes" % (off, n, self.mapped_size))
        if write and self.readonly:
            raise PermissionError("read-only view")

    def read(self, off: int, n: int) -> bytes:
        self._chk(off, n)
        return ctypes.string_at(self.base + off, n)

    def write(self, off: int, data: bytes):
        self._chk(off, len(data), True)
        ctypes.memmove(self.base + off, data, len(data))

    def zero(self, off: int, n: int):
        self._chk(off, n, True)
        ctypes.memset(self.base + off, 0, n)

    def u16(self, off):
        self._chk(off, 2)
        return ctypes.c_uint16.from_address(self.base + off).value

    def set_u16(self, off, v):
        self._chk(off, 2, True)
        ctypes.c_uint16.from_address(self.base + off).value = v & 0xFFFF

    def u32(self, off):
        self._chk(off, 4)
        return ctypes.c_uint32.from_address(self.base + off).value

    def set_u32(self, off, v):
        self._chk(off, 4, True)
        ctypes.c_uint32.from_address(self.base + off).value = v & MASK32

    def u64(self, off):
        self._chk(off, 8)
        return ctypes.c_uint64.from_address(self.base + off).value

    def set_u64(self, off, v):
        self._chk(off, 8, True)
        ctypes.c_uint64.from_address(self.base + off).value = v & MASK64

    def cas_u32(self, off: int, expected: int, new: int) -> int:
        """Atomic compare-and-swap (lock cmpxchg): stores `new` only if the word equals `expected`; returns the
 value found (== expected on success)."""
        self._chk(off, 4, True)
        if off & 3:
            raise ValueError("cas_u32 needs a 4-byte aligned offset")
        return int(_cas32()(self.base + off, new & MASK32, expected & MASK32))

    # -- PID + generation pairs: one aligned u64, low word = PID, high word = generation ----------
    _IDENTITY = {"sn": 0x040, "se": 0x080}

    def identity(self, side: str):
        """(pid, generation) of the SN or SE block, read as one u64 (never a new PID with an old generation)."""
        v = self.u64(self._IDENTITY[side])
        return v & MASK32, v >> 32

    def set_identity(self, side: str, pid: int, generation: int):
        self.set_u64(self._IDENTITY[side], ((generation & MASK32) << 32) | (pid & MASK32))

    # -- header words (C0) ----------------------------------------------------------------------------------------
    def hdr(self, name: str) -> int:
        f = L.HDR.field(name)
        return {2: self.u16, 4: self.u32, 8: self.u64}[f.size](f.offset)

    def set_hdr(self, name: str, v: int):
        f = L.HDR.field(name)
        {2: self.set_u16, 4: self.set_u32, 8: self.set_u64}[f.size](f.offset, v)

    def header(self) -> dict:
        return L.HDR.unpack(self.read(0, L.HDR.size))

    def region_table(self, count: int = None) -> list:
        n = self.hdr("regionCount") if count is None else count
        n = max(0, min(n, (L.HEADER_SIZE - L.REGION_TABLE_OFFSET) // L.REGION_ENTRY_SIZE))
        raw = self.read(L.REGION_TABLE_OFFSET, n * L.REGION_ENTRY_SIZE)
        return [L.REGION_ENTRY.unpack(raw, i * L.REGION_ENTRY_SIZE) for i in range(n)]

    # -- rings ----------------------------------------------------------------------------------------------------
    def ring_head(self, key: str) -> int:
        return self.u64(L.ring_head_offset(key))

    def ring_tail(self, key: str) -> int:
        return self.u64(L.ring_tail_offset(key))

    def ring_heads(self) -> dict:
        return {r.key: self.ring_head(r.key) for r in L.RINGS}

    def ring_tails(self) -> dict:
        return {r.key: self.ring_tail(r.key) for r in L.RINGS}

    def resync_tails(self, keys) -> dict:
        """Consumer re-sync (CONTRACT 5.2): tail:= head for each ring; heads are only read. Ring objects built on
 this section keep their own cached tail: call their resync_tail instead (or rebuild them)."""
        out = {}
        for k in keys:
            h = self.ring_head(k)
            self.set_u64(L.ring_tail_offset(k), h)
            out[k] = h
        return out

    # -- checksums (fast BLAKE2b-64 for "unchanged" checks; FNV-1a 64 available for small ranges) -----------------
    def region_checksums(self, keys=None) -> dict:
        out = {}
        for r in L.REGIONS:
            if keys is not None and r.key not in keys:
                continue
            if r.offset + r.size <= self.mapped_size:
                out[r.key] = hashlib.blake2b(self.read(r.offset, r.size), digest_size=8).hexdigest()
        out["all"] = hashlib.blake2b(self.read(0, self.mapped_size), digest_size=8).hexdigest()
        return out

    def se_owned_checksum(self) -> str:
        """BLAKE2b-64 over every byte the SE side may write: C0 SE block and devControl, the heads of the rings SE
 produces (C3, C4, C11b), the tails of the rings it consumes (C8, C12, C11a), C1, C2, C5, C6 and the C3/C4/C11b
 ring data. S0.M1: a rejecting SE leaves this unchanged."""
        if self.mapped_size < L.SECTION_SIZE:
            return hashlib.blake2b(self.read(0, self.mapped_size), digest_size=8).hexdigest()
        h = hashlib.blake2b(digest_size=8)
        h.update(self.read(0x080, 0x80))
        h.update(self.read(L.HDR.offset("devControl"), 4))
        for k in ("C3", "C4", "C11b"):
            h.update(self.read(L.ring_head_offset(k), 8))
        for k in ("C8", "C12", "C11a"):
            h.update(self.read(L.ring_tail_offset(k), 8))
        for k in ("C1", "C2", "C5", "C6", "C3", "C4", "C11b"):
            g = L.REGION[k]
            h.update(self.read(g.offset, g.size))
        return h.hexdigest()

def _view_size(view, fallback: int) -> int:
    mbi = MEMORY_BASIC_INFORMATION()
    n = _VirtualQuery(view, ctypes.byref(mbi), ctypes.sizeof(mbi))
    if not n:
        return fallback
    return int(mbi.RegionSize)

def section_exists(name: str):
    """(state, err): 'absent' (ERROR_FILE_NOT_FOUND), 'present', or 'error'. Opens with SECTION_QUERY only."""
    full = Section.full_name(name)
    h = _OpenFileMappingW(FILE_MAP_READ, False, full)
    if h:
        _CloseHandle(h)
        return "present", 0
    err = last_error()
    if err == ERROR_FILE_NOT_FOUND:
        return "absent", err
    if err == ERROR_ACCESS_DENIED:
        return "present", err
    return "error", err

# ----------------------------------------------------------------------------------------------------------------
# Seqlock (CONTRACT 5.3): seq is the first u32 of the record
# ----------------------------------------------------------------------------------------------------------------

SEQ_FRESH, SEQ_UNCHANGED, SEQ_BUSY, SEQ_NEVER = "fresh", "unchanged", "busy", "never"

def seqlock_write(sec: Section, off: int, record_bytes: bytes):
    """Publishes a whole record (its first 4 bytes, the seq slot, are ignored). Continues from the seq found:
 odd, payload, even; never publishes 0 (a restarted writer never resets seq)."""
    found = sec.u32(off)
    odd = found | 1
    sec.set_u32(off, odd)              # x86-64 TSO: this store is not reordered with the payload stores below
    sec.write(off + 4, record_bytes[4:])
    even = (odd + 1) & MASK32
    if even == 0:
        even = 2
    sec.set_u32(off, even)

def seqlock_read(sec: Section, off: int, size: int, tries: int = L.SEQLOCK_MAX_TRIES):
    """(status, seq, bytes): 'fresh' with a coherent copy, 'busy' (odd or changed every try), 'never' (seq 0)."""
    for _ in range(tries):
        s1 = sec.u32(off)
        if s1 == 0:
            return SEQ_NEVER, 0, None
        if s1 & 1:
            continue
        data = sec.read(off, size)
        s2 = sec.u32(off)
        if s1 == s2:
            return SEQ_FRESH, s1, data
    return SEQ_BUSY, 0, None

class SeqlockReader:
    """Keeps the last coherent copy of one record (CONTRACT 5.1: else keeps its last coherent copy)."""

    def __init__(self, sec: Section, off: int, record: L.Record):
        self.sec, self.off, self.record = sec, off, record
        self.value = None
        self.last_seq = 0
        self.busy = 0

    def poll(self) -> str:
        st, seq, data = seqlock_read(self.sec, self.off, self.record.size)
        if st == SEQ_FRESH:
            if seq == self.last_seq and self.value is not None:
                return SEQ_UNCHANGED
            self.value = self.record.unpack(data)
            self.last_seq = seq
            return SEQ_FRESH
        if st == SEQ_BUSY:
            self.busy += 1
        return st

    def reset(self):
        self.value, self.last_seq = None, 0

# ----------------------------------------------------------------------------------------------------------------
# Rings (CONTRACT 5.3). Producers keep their head locally and never zero it; consumers keep their tail locally.
# Build ring objects AFTER the creator/attach step.
# ----------------------------------------------------------------------------------------------------------------

RW_OK, RW_FULL, RW_TOO_LARGE, RW_INVALID, RW_CORRUPT = "ok", "full", "too_large", "invalid_type", "corrupt"
RR_OK, RR_EMPTY, RR_CORRUPT = "ok", "empty", "corrupt"

class ByteRing:
    """SPSC byte ring: {u32 type, u32 payloadBytes} + payload padded to 8; a type-0 pad record (payloadBytes = bytes
 to the end - 8) before a message that would cross the end; messages over half the ring rejected."""

    def __init__(self, sec: Section, key: str):
        g = L.REGION[key]
        if g.kind != L.KIND_BYTE_RING:
            raise ValueError("%s is not a byte ring" % key)
        self.sec, self.key = sec, key
        self.head_off, self.tail_off = L.ring_head_offset(key), L.ring_tail_offset(key)
        self.data_off, self.capacity = g.offset, g.size
        self.mask = g.size - 1
        self.producer_head = sec.u64(self.head_off)
        self.consumer_tail = sec.u64(self.tail_off)
        self.corrupt = 0
        self.dropped = 0

    # producer
    def try_write(self, mtype: int, payload: bytes) -> str:
        if mtype == L.PAD_TYPE:
            return RW_INVALID
        n = len(payload)
        need = L.framed_size(n)
        if need > self.capacity // 2:
            return RW_TOO_LARGE
        h = self.producer_head
        t = self.sec.u64(self.tail_off)
        used = (h - t) & MASK64
        if used > self.capacity or h & 7:
            self.corrupt += 1          # impossible counters: never overwrite
            return RW_CORRUPT
        off = h & self.mask
        contiguous = self.capacity - off
        pad = need > contiguous
        total = contiguous + need if pad else need
        if used + total > self.capacity:
            return RW_FULL
        if pad:
            self.sec.write(self.data_off + off, struct.pack("<II", L.PAD_TYPE, contiguous - 8))
            off = 0
        body = struct.pack("<II", mtype, n) + payload + b"\0" * (need - 8 - n)
        self.sec.write(self.data_off + off, body)
        h = (h + total) & MASK64
        self.sec.set_u64(self.head_off, h)   # release (TSO): the message is complete before the head moves
        self.producer_head = h
        return RW_OK

    def try_write_or_drop(self, mtype: int, payload: bytes) -> str:
        st = self.try_write(mtype, payload)
        if st != RW_OK:
            self.dropped += 1
        return st

    def free(self) -> int:
        used = (self.producer_head - self.sec.u64(self.tail_off)) & MASK64
        return 0 if used > self.capacity else self.capacity - used

    # consumer
    def try_read(self):
        """(status, type, payload). Pads are consumed. 'corrupt' consumes nothing (call resync_tail)."""
        for _ in range(2):
            t = self.consumer_tail
            h = self.sec.u64(self.head_off)
            if h == t:
                return RR_EMPTY, 0, b""
            avail = (h - t) & MASK64
            if avail > self.capacity or avail & 7 or t & 7 or avail < 8:
                self.corrupt += 1
                return RR_CORRUPT, 0, b""
            off = t & self.mask
            mtype, n = struct.unpack("<II", self.sec.read(self.data_off + off, 8))
            if mtype == L.PAD_TYPE:
                skip = self.capacity - off
                if off == 0 or skip > avail:
                    self.corrupt += 1
                    return RR_CORRUPT, 0, b""
                t += skip
                self.sec.set_u64(self.tail_off, t)
                self.consumer_tail = t
                continue
            need = L.framed_size(n)
            if need > self.capacity - off or need > avail or need > self.capacity // 2:
                self.corrupt += 1
                return RR_CORRUPT, 0, b""
            payload = self.sec.read(self.data_off + off + 8, n)
            t += need
            self.sec.set_u64(self.tail_off, t)    # the payload was copied before the space is given back
            self.consumer_tail = t
            return RR_OK, mtype, payload
        self.corrupt += 1
        return RR_CORRUPT, 0, b""

    def resync_tail(self) -> int:
        h = self.sec.u64(self.head_off)
        self.sec.set_u64(self.tail_off, h)
        self.consumer_tail = h
        return h

    def used(self) -> int:
        n = (self.sec.u64(self.head_off) - self.sec.u64(self.tail_off)) & MASK64
        return 0 if n > self.capacity else n

class EntryRing:
    """C8: SPSC fixed-record ring; slot = counter mod capacity; full when head - tail = capacity (record counters)."""

    def __init__(self, sec: Section, key: str = "C8"):
        g = L.REGION[key]
        if g.kind != L.KIND_RECORD_RING:
            raise ValueError("%s is not a record ring" % key)
        self.sec, self.key = sec, key
        self.head_off, self.tail_off = L.ring_head_offset(key), L.ring_tail_offset(key)
        self.data_off, self.record_size = g.offset, g.record_size
        self.capacity = g.size // g.record_size
        self.producer_head = sec.u64(self.head_off)
        self.consumer_tail = sec.u64(self.tail_off)
        self.corrupt = 0

    def try_write(self, record: bytes) -> str:
        if len(record) != self.record_size:
            raise ValueError("record size")
        h = self.producer_head
        t = self.sec.u64(self.tail_off)
        used = (h - t) & MASK64
        if used > self.capacity:
            self.corrupt += 1
            return RW_CORRUPT
        if used == self.capacity:
            return RW_FULL
        self.sec.write(self.data_off + (h % self.capacity) * self.record_size, record)
        h = (h + 1) & MASK64
        self.sec.set_u64(self.head_off, h)
        self.producer_head = h
        return RW_OK

    def try_read(self):
        t = self.consumer_tail
        h = self.sec.u64(self.head_off)
        if h == t:
            return RR_EMPTY, b""
        if ((h - t) & MASK64) > self.capacity:
            self.corrupt += 1
            return RR_CORRUPT, b""
        data = self.sec.read(self.data_off + (t % self.capacity) * self.record_size, self.record_size)
        t = (t + 1) & MASK64
        self.sec.set_u64(self.tail_off, t)
        self.consumer_tail = t
        return RR_OK, data

    def resync_tail(self) -> int:
        h = self.sec.u64(self.head_off)
        self.sec.set_u64(self.tail_off, h)
        self.consumer_tail = h
        return h

class OverflowQueue:
    """C8 producer policy: up to 4,096 queued records, then drop the oldest and count."""

    def __init__(self, capacity: int = L.C8_OVERFLOW_CAPACITY):
        import collections
        self.items = collections.deque()
        self.capacity = capacity
        self.dropped = 0

    def enqueue(self, rec: bytes):
        if len(self.items) >= self.capacity:
            self.items.popleft()
            self.dropped += 1
        self.items.append(rec)

    def flush_to(self, ring: EntryRing) -> int:
        n = 0
        while self.items:
            if ring.try_write(self.items[0]) != RW_OK:
                break
            self.items.popleft()
            n += 1
        return n

# ----------------------------------------------------------------------------------------------------------------
# Validation on both sides (CONTRACT 6, steps 1-5; step 6 is the caller's self-test flag)
# ----------------------------------------------------------------------------------------------------------------

def validate(sec: Section, selftest_pass: bool = True, check_region_table: bool = False) -> dict:
    """{'outcome': 'ok'|'not_ready'|'reject', 'step', 'field', 'expected', 'found', 'notes'}. Reads only.
 check_region_table: also compare the C0 region table with the layout (step 7, not a CONTRACT step; the creator
 rule 2 of the stand-in uses it, like the C# SectionCreator)."""
    r = {"outcome": "ok", "step": 0, "field": None, "expected": None, "found": None, "notes": []}

    def fail(outcome, step, field, expected, found):
        r.update(outcome=outcome, step=step, field=field, expected=expected, found=found)
        return r

    if sec.mapped_size < L.HEADER_SIZE:
        return fail("reject", 1, "sectionSize", hex(L.SECTION_SIZE), hex(sec.mapped_size) + " (mapped)")
    magic = sec.hdr("magic")
    if magic == 0:
        return fail("not_ready", 1, "magic", hex(L.MAGIC), "0x0")
    if magic != L.MAGIC:
        return fail("reject", 1, "magic", hex(L.MAGIC), hex(magic))
    for name, want in (("byteOrderMark", L.BYTE_ORDER_MARK), ("headerSize", L.HEADER_SIZE),
                       ("sectionSize", L.SECTION_SIZE)):
        v = sec.hdr(name)
        if v != want:
            return fail("reject", 1, name, hex(want), hex(v))
    if sec.mapped_size != L.SECTION_SIZE:
        return fail("reject", 1, "sectionSize", hex(L.SECTION_SIZE), hex(sec.mapped_size) + " (mapped)")
    major = sec.hdr("layoutMajor")
    if major != L.LAYOUT_MAJOR:
        return fail("reject", 2, "layoutMajor", str(L.LAYOUT_MAJOR), str(major))
    minor = sec.hdr("layoutMinor")
    if minor != L.LAYOUT_MINOR:
        r["notes"].append("layoutMinor differs: local %d, section %d (accepted)" % (L.LAYOUT_MINOR, minor))
    h = sec.hdr("layoutHash")
    mine = L.layout_hash()
    if h != mine:
        return fail("reject", 3, "layoutHash", L.hex64(mine), L.hex64(h))
    errs = L.selftest(verbose=False)
    if errs:
        return fail("reject", 3, "recordSizes", "layout selftest PASS", errs[0])
    for name, want in (("wireVersion", L.WIRE_VERSION), ("siteVersion", L.SITE_VERSION),
                       ("damageScaleVersion", L.DAMAGE_SCALE_VERSION)):
        v = sec.hdr(name)
        if v != want:
            return fail("reject", 4, name, str(want), str(v))
    q = sec.hdr("qpcFrequency")
    if q != QPC_FREQUENCY:
        return fail("reject", 5, "qpcFrequency", str(QPC_FREQUENCY), str(q))
    if not selftest_pass:
        return fail("reject", 6, "wm1SelfTest", "PASS", "FAIL")
    if check_region_table:
        n = sec.hdr("regionCount")
        if n != len(L.REGIONS):
            return fail("reject", 7, "regionCount", str(len(L.REGIONS)), str(n))
        mine = L.region_table_bytes()
        found = sec.read(L.REGION_TABLE_OFFSET, len(mine))
        for i in range(len(L.REGIONS)):
            a = mine[i * L.REGION_ENTRY_SIZE:(i + 1) * L.REGION_ENTRY_SIZE]
            b = found[i * L.REGION_ENTRY_SIZE:(i + 1) * L.REGION_ENTRY_SIZE]
            if a != b:
                return fail("reject", 7, "region[%d]" % i, a.hex(), b.hex())
    return r

def describe_validation(r: dict) -> str:
    if r["outcome"] == "ok":
        return "OK" + ("; " + "; ".join(r["notes"]) if r["notes"] else "")
    return "step %d field=%s expected=%s found=%s" % (r["step"], r["field"], r["expected"], r["found"])

# ----------------------------------------------------------------------------------------------------------------
# Mutexes, named objects, processes
# ----------------------------------------------------------------------------------------------------------------

class NamedMutex:
    """Owned on the acquiring thread; release on the same thread."""

    def __init__(self, name, handle):
        self.name, self.handle = name, handle

    @classmethod
    def try_acquire(cls, name: str):
        """(status, mutex|None, err): 'acquired', 'abandoned', 'held', 'error'."""
        h = _CreateMutexW(None, False, name)
        err = last_error()
        if not h:
            return "error", None, err
        r = _WaitForSingleObject(h, 0)
        if r == WAIT_OBJECT_0:
            return "acquired", cls(name, h), 0
        if r == WAIT_ABANDONED:
            return "abandoned", cls(name, h), 0
        _CloseHandle(h)
        if r == WAIT_TIMEOUT:
            return "held", None, 0
        return "error", None, last_error()

    def release(self):
        if self.handle:
            _ReleaseMutex(self.handle)
            _CloseHandle(self.handle)
            self.handle = None

class NamedObjectHolder:
    """Keeps a named mutex object alive. Created owned."""

    def __init__(self, name: str):
        self.name = name
        self.handle = _CreateMutexW(None, True, name)
        self.err = last_error()
        if not self.handle:
            raise win_error(self.err, "CreateMutexW(%s)" % name)
        self.existed = self.err == ERROR_ALREADY_EXISTS

    def close(self):
        if self.handle:
            _ReleaseMutex(self.handle)
            _CloseHandle(self.handle)
            self.handle = None

def named_object_present(name: str):
    """(present, err): any named object of that name (a different type answers ERROR_INVALID_HANDLE)."""
    h = _OpenMutexW(SYNCHRONIZE, False, name)
    if h:
        _CloseHandle(h)
        return True, 0
    err = last_error()
    if err == ERROR_FILE_NOT_FOUND:
        return False, err
    if err in (ERROR_INVALID_HANDLE, ERROR_ACCESS_DENIED):
        return True, err
    return False, err

def process_state(pid: int) -> str:
    """'alive', 'exited', 'notfound', 'denied', 'error' (OpenProcess(SYNCHRONIZE) + WaitForSingleObject(h, 0))."""
    if pid <= 0:
        return "notfound"
    h = _OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        err = last_error()
        if err == ERROR_INVALID_PARAMETER:
            return "notfound"
        if err == ERROR_ACCESS_DENIED:
            return "denied"
        return "error"
    try:
        r = _WaitForSingleObject(h, 0)
        if r == WAIT_OBJECT_0:
            return "exited"
        if r == WAIT_TIMEOUT:
            return "alive"
        return "error"
    finally:
        _CloseHandle(h)

def is_process_alive(pid: int) -> bool:
    """Conservative for the creator rule 2: anything but 'notfound'/'exited' counts as alive."""
    return process_state(pid) not in ("notfound", "exited")

class ProcessWatch:
    """A SYNCHRONIZE handle on a peer process: exited is WaitForSingleObject(h, 0) (a few microseconds)."""

    def __init__(self, pid: int):
        self.pid = pid
        self.handle = _OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        self.err = 0 if self.handle else last_error()

    def exited(self) -> bool:
        if not self.handle:
            return True
        return _WaitForSingleObject(self.handle, 0) == WAIT_OBJECT_0

    def wait(self, timeout_s: float) -> bool:
        if not self.handle:
            return True
        return _WaitForSingleObject(self.handle, int(max(0, timeout_s) * 1000)) == WAIT_OBJECT_0

    def exit_code(self):
        if not self.handle:
            return None
        c = wt.DWORD()
        if _GetExitCodeProcess(self.handle, ctypes.byref(c)):
            return None if c.value == STILL_ACTIVE else int(c.value)
        return None

    def close(self):
        if self.handle:
            _CloseHandle(self.handle)
            self.handle = None

def terminate_pid(pid: int, code: int = 1) -> bool:
    """TerminateProcess by exact PID. Callers in this repo use it only on their own stand-in processes, or after the
 project's explicit confirmation (CONTRACT 7)."""
    h = _OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, False, pid)
    if not h:
        return False
    try:
        ok = bool(_TerminateProcess(h, code))
        _WaitForSingleObject(h, 5000)
        return ok
    finally:
        _CloseHandle(h)

def list_processes() -> list:
    """[{pid, ppid, threads, name}] via Toolhelp32."""
    snap = _CreateToolhelp32Snapshot(0x2, 0)
    if not snap or snap == INVALID_HANDLE_VALUE:
        raise win_error(last_error(), "CreateToolhelp32Snapshot")
    out = []
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = _Process32FirstW(snap, ctypes.byref(e))
        while ok:
            out.append({"pid": int(e.th32ProcessID), "ppid": int(e.th32ParentProcessID),
                        "threads": int(e.cntThreads), "name": e.szExeFile})
            ok = _Process32NextW(snap, ctypes.byref(e))
    finally:
        _CloseHandle(snap)
    return out

def process_image_path(pid: int):
    h = _OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        n = wt.DWORD(1024)
        if _QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
            return buf.value
        return None
    finally:
        _CloseHandle(h)

def token_elevation(pid: int):
    """(elevated: bool|None, integrity RID: int|None) of a process token (0x2000 medium, 0x3000 high)."""
    h = _OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None, None
    tok = HANDLE()
    try:
        if not _OpenProcessToken(h, TOKEN_QUERY, ctypes.byref(tok)):
            return None, None
        try:
            elevated = None
            try:
                buf = _token_info(tok, 20)  # TokenElevation
                elevated = bool(ctypes.c_uint32.from_buffer(buf, 0).value)
            except OSError:
                pass
            rid = None
            try:
                buf = _token_info(tok, 25)  # TokenIntegrityLevel: TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES }
                psid = ctypes.c_void_p.from_buffer(buf, 0).value
                count = _GetSidSubAuthorityCount(psid).contents.value
                rid = int(_GetSidSubAuthority(psid, count - 1).contents.value)
            except (OSError, ValueError):
                pass
            return elevated, rid
        finally:
            _CloseHandle(tok)
    finally:
        _CloseHandle(h)

def memory_status() -> dict:
    m = MEMORYSTATUSEX()
    m.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
    if not _GlobalMemoryStatusEx(ctypes.byref(m)):
        raise win_error(last_error(), "GlobalMemoryStatusEx")
    return {"total": int(m.ullTotalPhys), "available": int(m.ullAvailPhys), "load": int(m.dwMemoryLoad)}

# ----------------------------------------------------------------------------------------------------------------
# Windows (user32)
# ----------------------------------------------------------------------------------------------------------------

GW_OWNER = 4
WM_CLOSE = 0x0010

def top_level_windows(pid: int = None) -> list:
    """[] of top-level windows (optionally of one process)."""
    out = []

    def cb(hwnd, _lp):
        p = wt.DWORD()
        _GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if pid is None or p.value == pid:
            buf = ctypes.create_unicode_buffer(256)
            _GetWindowTextW(hwnd, buf, 256)
            out.append((int(hwnd or 0), int(p.value), bool(_IsWindowVisible(hwnd)), int(_GetWindow(hwnd, GW_OWNER) or 0),
                        buf.value))
        return True

    _EnumWindows(WNDENUMPROC(cb), 0)
    return out

def main_window(pid: int):
    """The.NET Process.MainWindowHandle rule: the first visible top-level window without an project."""
    for hwnd, _p, visible, owner, _t in top_level_windows(pid):
        if visible and owner == 0:
            return hwnd
    return None

def close_main_window(pid: int) -> bool:
    """CloseMainWindow: posts WM_CLOSE to the process's main window. False when it has none."""
    hwnd = main_window(pid)
    if not hwnd:
        return False
    return bool(_PostMessageW(hwnd, WM_CLOSE, 0, 0))

def allow_set_foreground_window(pid: int):
    ok = bool(_AllowSetForegroundWindow(pid))
    return ok, (0 if ok else last_error())

def foreground_pid():
    hwnd = _GetForegroundWindow()
    if not hwnd:
        return None, None
    p = wt.DWORD()
    _GetWindowThreadProcessId(hwnd, ctypes.byref(p))
    return int(p.value), int(hwnd)

def is_iconic(hwnd) -> bool:
    return bool(_IsIconic(hwnd))

# ----------------------------------------------------------------------------------------------------------------
# Holders of a named section (handle enumeration)
# ----------------------------------------------------------------------------------------------------------------

STATUS_INFO_LENGTH_MISMATCH = ctypes.c_long(0xC0000004).value
SYSTEM_EXTENDED_HANDLE_INFORMATION = 64
_ENTRY = struct.Struct("<QQQIHHII")  # Object, UniqueProcessId, HandleValue, GrantedAccess, CreatorBackTraceIndex,
 # ObjectTypeIndex, HandleAttributes, Reserved (40 bytes on x64)

def section_holders(name: str):
    """(state, pids, note): state 'absent' | 'present' | 'error'. pids: processes other than this one holding a
 handle to the section (NtQuerySystemInformation(SystemExtendedHandleInformation), matched by the kernel object
 address of our own handle). A process that mapped a view and closed its handle keeps the section alive without
 holding a handle and is not listed. On builds that hide kernel addresses the list is unknown (pids None)."""
    full = Section.full_name(name)
    h = _OpenFileMappingW(FILE_MAP_READ, False, full)
    if not h:
        err = last_error()
        if err == ERROR_FILE_NOT_FOUND:
            return "absent", [], ""
        return ("present" if err == ERROR_ACCESS_DENIED else "error"), None, "OpenFileMappingW error %d" % err
    try:
        size = 1 << 22
        for _ in range(8):
            buf = ctypes.create_string_buffer(size)
            ret = wt.ULONG(0)
            st = _NtQuerySystemInformation(SYSTEM_EXTENDED_HANDLE_INFORMATION, buf, size, ctypes.byref(ret))
            if st == STATUS_INFO_LENGTH_MISMATCH:
                size = max(size * 2, int(ret.value) + (1 << 20))
                continue
            if st < 0:
                return "present", None, "NtQuerySystemInformation status 0x%08x" % (st & MASK32)
            break
        else:
            return "present", None, "handle table too large"
        raw = buf.raw
        count = struct.unpack_from("<Q", raw, 0)[0]
        me, myh = current_pid(), int(h)
        entries = _ENTRY.iter_unpack(raw[16:16 + count * _ENTRY.size])
        obj = None
        rows = []
        for e in entries:
            if e[3] == 0 and e[1] == 0:
                continue
            rows.append((e[0], e[1]))
            if e[1] == me and e[2] == myh:
                obj = e[0]
        if not obj:
            return "present", None, "kernel object addresses are not visible to this process"
        pids = sorted(set(p for (o, p) in rows if o == obj and p != me))
        return "present", pids, ""
    finally:
        _CloseHandle(h)

# ----------------------------------------------------------------------------------------------------------------
# C11 harness side (dev control): JSON lines on CtlToSe/CtlToSn, replies on CtlFromSe/CtlFromSn
# ----------------------------------------------------------------------------------------------------------------

HARNESS_BIT = 1 << L.DEV_CONTROL["harness"]
RELAY_BIT = 1 << L.DEV_CONTROL["seRelay"]

def acquire_harness_bit(sec: Section, mutant_rmw: bool = False):
    """devControl bit 0 by compare-and-swap, only while bit 1 (the SE relay) and bit 0 are clear: (ok, why).
 The SE plugin takes bit 1 with Interlocked.CompareExchange only while bit 0 is clear, so both writers of the word
 are atomic and the bits stay exclusive.
 mutant_rmw: the pre-review read-then-write (seeded failure for tests/wire/cross_test.py S0.DEV.cas)."""
    off = L.HDR.offset("devControl")
    if mutant_rmw:
        dc = sec.u32(off)
        if dc & RELAY_BIT:
            return False, "devControl bit 1 set: the SE relay owns CtlToSn"
        if dc & HARNESS_BIT:
            return False, "devControl bit 0 already set: another harness is attached"
        sec.set_u32(off, dc | HARNESS_BIT)
        time.sleep(0.02)
        dc2 = sec.u32(off)
        if dc2 & RELAY_BIT:
            sec.set_u32(off, dc2 & ~HARNESS_BIT)
            return False, "the SE relay attached at the same time"
        return True, ""
    for _ in range(64):
        cur = sec.u32(off)
        if cur & RELAY_BIT:
            return False, "devControl bit 1 set: the SE relay owns CtlToSn"
        if cur & HARNESS_BIT:
            return False, "devControl bit 0 already set: another harness is attached"
        if sec.cas_u32(off, cur, cur | HARNESS_BIT) == cur:
            return True, ""
    return False, "devControl contended (64 compare-and-swap attempts lost)"

def release_harness_bit(sec: Section, mutant_rmw: bool = False) -> bool:
    """Clears devControl bit 0 by compare-and-swap (never touches bit 1). True when it was set."""
    off = L.HDR.offset("devControl")
    if mutant_rmw:
        dc = sec.u32(off)
        sec.set_u32(off, dc & ~HARNESS_BIT)
        return bool(dc & HARNESS_BIT)
    while True:
        cur = sec.u32(off)
        if not cur & HARNESS_BIT:
            return False
        if sec.cas_u32(off, cur, cur & ~HARNESS_BIT & MASK32) == cur:
            return True

class Harness:
    """The Python test harness of CONTRACT 5.1: sets devControl bit 0 (refused while bit 1 is set), produces C11a and
 C11c, consumes C11b and C11d. devControl is one word shared with the SE relay bit: every change is an atomic
 compare-and-swap."""

    def __init__(self, sec: Section, mutant_rmw: bool = False):
        self.sec = sec
        self.mutant_rmw = mutant_rmw
        self.attached = False
        self.next_id = 1
        self.to_se = self.to_sn = self.from_se = self.from_sn = None
        self.replies = {}

    def attach(self):
        ok, why = acquire_harness_bit(self.sec, self.mutant_rmw)
        if not ok:
            return False, why
        self.to_se = ByteRing(self.sec, "C11a")
        self.to_sn = ByteRing(self.sec, "C11c")
        self.from_se = ByteRing(self.sec, "C11b")
        self.from_sn = ByteRing(self.sec, "C11d")
        self.from_se.resync_tail()
        self.from_sn.resync_tail()
        self.attached = True
        return True, ""

    def detach(self):
        if self.attached:
            release_harness_bit(self.sec, self.mutant_rmw)
            self.attached = False

    def send(self, side: str, cmd: str, args=None):
        import json
        rid = self.next_id
        self.next_id += 1
        msg = json.dumps({"id": rid, "cmd": cmd, "args": args or {}}, separators=(",", ":")).encode("utf-8")
        ring = self.to_sn if side == "sn" else self.to_se
        st = ring.try_write(L.C11_TYPES["Json"], msg)
        return rid, st

    def poll_replies(self):
        import json
        for ring in (self.from_sn, self.from_se):
            while True:
                st, mtype, payload = ring.try_read()
                if st != RR_OK:
                    if st == RR_CORRUPT:
                        ring.resync_tail()
                    break
                if mtype != L.C11_TYPES["Json"]:
                    continue
                try:
                    obj = json.loads(payload.decode("utf-8"))
                    self.replies[obj.get("id")] = obj
                except ValueError:
                    pass

    def wait_reply(self, rid: int, timeout_s: float):
        end = time.monotonic() + timeout_s
        while time.monotonic() < end:
            self.poll_replies()
            if rid in self.replies:
                return self.replies.pop(rid)
            time.sleep(0.01)
        return None
