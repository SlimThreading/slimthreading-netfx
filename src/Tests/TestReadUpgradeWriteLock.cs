// Copyright 2011 Carlos Martins
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//  
using System;
using System.Threading;
using SlimThreading;

namespace TestShared {

    internal class TestReadUpgradeWriteLock {

        //
        // The number of threads.
        //

        private const int READERS = 10;
        private const int WRITERS = 2;
        private const int UPGRADERS = 2;
        private const int XUPGRADERS = 2;

        private const int THREADS = (READERS + WRITERS + UPGRADERS + XUPGRADERS);

        //
        // The rendezvous channel.
        //

        private static StReadUpgradeWriteLock rwl =
                  new StReadUpgradeWriteLock(StLockRecursionPolicy.SupportsRecursion, 100);

        //
        // The alerter and the count down event used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(THREADS);

        //
        // The counters.
        //

        private static int[] reads = new int[READERS];
        private static int[] writes = new int[WRITERS];
        private static int[] upgrades = new int[UPGRADERS];
        private static int[] xupgrades = new int[XUPGRADERS];

        //
        // The reader thread
        //

        class Reader {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ rd #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                int count = 0;
                do {
                    try {
                        while (!rwl.TryEnterRead(new StCancelArgs(r.Next(5) + 1, shutdown))) {
                            fail++;
                        }
                        rwl.EnterRead();
                        Platform.SpinWait(100);
                        count++;
                        rwl.ExitRead();
                        rwl.ExitRead();
                        if ((++reads[id] % 5000) == 0) {
                            VConsole.Write("-r{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    Platform.YieldProcessor();
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ rd #{0} exiting: [{1}/{2}]", id, count, fail);
                done.Signal();
            }
        }
        //
        // The writer thread
        //

        class Writer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ wr #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                int count = 0;
                do {
                    try {
                        while (!rwl.TryEnterWrite(new StCancelArgs(r.Next(5) + 1, shutdown))) {
                            fail++;
                        }
                        rwl.EnterWrite();
                        count++;
                        Platform.SpinWait(50);
                        rwl.ExitWrite();
                        rwl.ExitWrite();
                        if ((++writes[id] % 5000) == 0) {
                            VConsole.Write("-w{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    Platform.YieldProcessor();
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ wr #{0} exiting: [{1}/{2}]", id, count, fail);
                done.Signal();
            }
        }

        //
        // The pure upgrader thread
        //

        class Upgrader {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ upg #{0} started...", id);
                Random r = new Random(id);
                int fail = 0;
                int count = 0;
                do {
                    try {
                        while (!rwl.TryEnterUpgradeableRead(new StCancelArgs(r.Next(5) + 1,
                                                                                 shutdown))) {
                            fail++;
                        }
                        rwl.EnterUpgradeableRead();
                        rwl.EnterWrite();
                        count++;
                        Platform.SpinWait(50);
                        rwl.ExitWrite();
                        rwl.ExitUpgradeableRead();
                        rwl.ExitUpgradeableRead();
                        if ((++upgrades[id] % 5000) == 0) {
                            VConsole.Write("-u{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    Platform.YieldProcessor();
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ upg #{0} exiting: [{1}/{2}]", id, count, fail);
                done.Signal();
            }
        }

        //
        // The extended upgrader thread
        //

        class XUpgrader {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ xupg #{0} started...", id);
                Random r = new Random(id);
                int fail = 0;
                int count = 0;
                do {
                    try {
                        while (!rwl.TryEnterUpgradeableRead(new StCancelArgs(r.Next(5) + 1,
                                                                                 shutdown))) {
                            fail++;
                        }
                        rwl.EnterRead();
                        rwl.EnterWrite();
                        count++;
                        Platform.SpinWait(50);
                        rwl.ExitUpgradeableRead();
                        rwl.ExitWrite();
                        rwl.ExitRead();
                        if ((++xupgrades[id] % 5000) == 0) {
                            VConsole.Write("-xu{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    Platform.YieldProcessor();
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ xupg #{0} exiting: [{1}/{2}]", id, count, fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < READERS; i++) {
                new Reader().Start(i, "rd #" + i);
            }
            for (int i = 0; i < WRITERS; i++) {
                new Writer().Start(i, "wr #" + i);
            }
            for (int i = 0; i < UPGRADERS; i++) {
                new Upgrader().Start(i, "upg #" + i);
            }
            for (int i = 0; i < XUPGRADERS; i++) {
                new XUpgrader().Start(i, "xupg #" + i);
            }
            Action stop = () => {
                shutdown.Set();
                done.Wait();
                VConsole.WriteLine("+++ stop completed!");
            };
            return stop;
        }
    }
}
