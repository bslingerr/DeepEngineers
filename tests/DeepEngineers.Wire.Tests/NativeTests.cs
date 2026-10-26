using System;
using System.Diagnostics;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The native wrappers of: clocks, process liveness (S0.T-L1s building blocks), mutexes, tokens.</summary>
    [TestClass]
    public class NativeTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void Clocks()
        {
            ulong t1 = Win32.TickCount64();
            long q1 = Win32.QpcNow();
            Thread.Sleep(20);
            ulong t2 = Win32.TickCount64();
            long q2 = Win32.QpcNow();
            Assert.IsTrue(t1 > 0 && t2 >= t1 + 10, "GetTickCount64 advances");
            Assert.IsTrue(q2 > q1);
            long f = Win32.QpcFrequency();
            Assert.IsTrue(f > 0);
            Assert.AreEqual(Stopwatch.Frequency, f, "same counter as Stopwatch");
            Assert.AreEqual((uint)Process.GetCurrentProcess().Id, Win32.CurrentProcessId());
        }

        [TestMethod]
        public void ProcessState_AliveExitedNotFound()
        {
            Assert.AreEqual(ProcessState.Alive, Win32.QueryProcess(Win32.CurrentProcessId()));
            Assert.IsTrue(Win32.IsProcessAlive(Win32.CurrentProcessId()));
            Assert.AreEqual(ProcessState.NotFound, Win32.QueryProcess(TestUtil.DeadPid));
            Assert.IsFalse(Win32.IsProcessAlive(TestUtil.DeadPid));
            Assert.AreEqual(ProcessState.NotFound, Win32.QueryProcess(0));
            Assert.IsFalse(Win32.IsProcessAlive(0));

            using (Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }))
            {
                child.WaitForExit();
                // our Process object keeps a handle, so the process object still exists: Exited, not NotFound
                Assert.AreEqual(ProcessState.Exited, Win32.QueryProcess((uint)child.Id));
                Assert.IsFalse(Win32.IsProcessAlive((uint)child.Id));
            }
        }

        [TestMethod]
        public void ProcessWatch_SeesTheExitWithinOneSecond()
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
            using (Process child = Process.Start(psi))
            {
                int err;
                using (ProcessWatch w = ProcessWatch.Open((uint)child.Id, out err))
                {
                    Assert.IsNotNull(w, "OpenProcess(SYNCHRONIZE) error " + err);
                    Assert.IsFalse(w.HasExited(), "running");
                    child.WaitForExit();
                    var sw = Stopwatch.StartNew();
                    while (!w.HasExited() && sw.ElapsedMilliseconds < 1000) Thread.Sleep(1);
                    Assert.IsTrue(w.HasExited(), "exit seen ≤ 1 s (non-blocking poll)");
                    TestContext.WriteLine("exit seen after " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
                }
            }
            int e2;
            Assert.IsNull(ProcessWatch.Open(TestUtil.DeadPid, out e2));
            Assert.AreEqual(87, e2, "ERROR_INVALID_PARAMETER");
        }

        [TestMethod]
        public void NamedMutex_SecondInstanceRefused_AbandonedReacquired()
        {
            string name = @"Local\DeepEngineers_v1_test_mutex_" + Guid.NewGuid().ToString("N");
            NamedMutex first;
            int err;
            Assert.AreEqual(MutexAcquire.Acquired, NamedMutex.TryAcquire(name, out first, out err));
            MutexAcquire second = MutexAcquire.Error;
            var t = new Thread(() => { NamedMutex m2; int e; second = NamedMutex.TryAcquire(name, out m2, out e); if (m2 != null) m2.Dispose(); });
            t.Start(); t.Join();
            Assert.AreEqual(MutexAcquire.HeldElsewhere, second, "a second instance logs REFUSE second instance");
            first.Dispose();
            t = new Thread(() => { NamedMutex m2; int e; second = NamedMutex.TryAcquire(name, out m2, out e); if (m2 != null) m2.Dispose(); });
            t.Start(); t.Join();
            Assert.AreEqual(MutexAcquire.Acquired, second, "free again after release");

            // the holder dies without releasing (thread exit) → the next instance gets it, abandoned
            NamedMutex keep = null;
            NamedMutex extraHandle;
            Assert.AreEqual(MutexAcquire.Acquired, NamedMutex.TryAcquire(name, out extraHandle, out err)); // keeps the object alive
            extraHandle.Dispose();
            t = new Thread(() => { int e; NamedMutex.TryAcquire(name, out keep, out e); }); // never released
            t.Start(); t.Join();
            Assert.IsNotNull(keep);
            NamedMutex after;
            Assert.AreEqual(MutexAcquire.AcquiredAbandoned, NamedMutex.TryAcquire(name, out after, out err));
            Assert.IsTrue(after.WasAbandoned);
            after.Dispose();
            keep.Dispose();
        }

        [TestMethod]
        public void NamedObject_AnyType_PresentOrAbsent()
        {
            int err;
            string name = @"Local\DeepEngineers_v1_test_obj_" + Guid.NewGuid().ToString("N");
            Assert.AreEqual(NamedObjectState.Absent, Win32.QueryNamedObject(name, out err));
            NamedMutex m;
            Assert.AreEqual(MutexAcquire.Acquired, NamedMutex.TryAcquire(name, out m, out err));
            Assert.AreEqual(NamedObjectState.Present, Win32.QueryNamedObject(name, out err), "a mutex");
            m.Dispose();
            Assert.AreEqual(NamedObjectState.Absent, Win32.QueryNamedObject(name, out err));
            bool existed;
            using (SharedSection s = SharedSection.Create(name, 4096, TestUtil.Sddl, out existed))
                Assert.AreEqual(NamedObjectState.Present, Win32.QueryNamedObject(name, out err), "a file mapping (other type): error " + err);
            using (var ev = new EventWaitHandle(false, EventResetMode.ManualReset, name))
                Assert.AreEqual(NamedObjectState.Present, Win32.QueryNamedObject(name, out err), "an event");
        }

        [TestMethod]
        public void BridgeToken_EnvironmentVariable()
        {
            string old = Environment.GetEnvironmentVariable(WireInfo.BridgeEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(WireInfo.BridgeEnvVar, "1");
                string which;
                Assert.IsTrue(BridgeToken.IsPresent(out which));
                StringAssert.Contains(which, "DEEPENGINEERS_BRIDGE=1");
                Environment.SetEnvironmentVariable(WireInfo.BridgeEnvVar, "0");
                int err;
                bool launcher = Win32.QueryNamedObject(WireInfo.LauncherObject, out err) == NamedObjectState.Present;
                Assert.AreEqual(launcher, BridgeToken.IsPresent(out which), "only =1 counts; else the launcher object decides");
            }
            finally { Environment.SetEnvironmentVariable(WireInfo.BridgeEnvVar, old); }
        }

        [TestMethod]
        public void CurrentUserSid_FromTheToken()
        {
            string sid = Win32.CurrentUserSid();
            StringAssert.StartsWith(sid, "S-1-5-");
            Assert.AreEqual(System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, sid);
        }

        [TestMethod]
        public void PowerThrottlingOptOut_GhostingAndForeground_DoNotThrow()
        {
            PowerThrottlingResult r = Win32.OptOutOfPowerThrottling();
            TestContext.WriteLine("SetProcessInformation(ProcessPowerThrottling): " + r);
            Assert.IsTrue(r.Ok, r.ToString());
            Assert.IsTrue(r.ControlMask == 0x5 || r.ControlMask == 0x1);
            if (Environment.OSVersion.Version.Build >= 22000) Assert.AreEqual(0x5u, r.ControlMask, "Windows 11 accepts IGNORE_TIMER_RESOLUTION");

            string error;
            Assert.IsTrue(Win32.DisableProcessWindowsGhosting(out error), error);
            int e;
            bool ok = Win32.AllowSetForegroundWindow(Win32.CurrentProcessId(), out e);
            TestContext.WriteLine("AllowSetForegroundWindow(self) = " + ok + " error " + e);
            Assert.AreEqual(0xFFFFFFFFu, Win32.AsfwAny);
        }

        [TestMethod]
        public void SecurityDescriptor_FromSddl_RejectsGarbage()
        {
            IntPtr sd = Win32.SecurityDescriptorFromSddl(WireInfo.SectionSddlForCurrentUser());
            Assert.AreNotEqual(IntPtr.Zero, sd);
            Win32.FreeSecurityDescriptor(sd);
            try { Win32.SecurityDescriptorFromSddl("D:P(this is not sddl)"); Assert.Fail(); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
