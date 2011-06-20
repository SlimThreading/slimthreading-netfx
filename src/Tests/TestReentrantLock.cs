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

	class TestReentrantLock {

        //
        // The number of threads.
        //

		const int THREADS = 20;

        //
        // The lock and the recursive acquisition count.
        //
       
        const int REENTER = 5;
        static StReentrantLock _lock = new StReentrantLock(0);
        static object _clrLock = new object();

        //
        // The alerter and the count down latch used for shutdown.
        //

        static StAlerter shutdown = new StAlerter();
        static StCountDownEvent done = new StCountDownEvent(THREADS);

        //
        // The counters.
        //

		static int[] counts = new int[THREADS];

        //
        // ...
        //

        static volatile int sharedRandom;
        const int P = 100;

        //
        // Enter/exit thread using a SlimThreading Lock.
        //

        class EnterExit {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ e/x #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                int localRandom = r.Next();
                do {
                    if ((localRandom % 100) < P) {
                        while (!_lock.TryEnter(/*StCancelArgs.None*/ new StCancelArgs(10))) {
                            fail++;
                        }
                        for (int i = 1; i < REENTER; i++) {
                            _lock.Enter();
                        }
                        localRandom = sharedRandom = r.Next();
                        Platform.SpinWait(100);
                        for (int i = 1; i < REENTER; i++) {
                            _lock.Exit();
                        }
                        _lock.Exit();
                    } else {
                        localRandom = r.Next();
                    }
                    if ((++counts[id] % 50000) == 0) {
                        VConsole.Write("-{0}", id);
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ a/r #{0} exiting, after {1}[{2}] enter/exit",
                                    id, counts[id], fail);
                done.Signal();
            }
        }

        //
        // Enter/exit thread using a CLR lock.
        //

        class EnterExit2 {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ e/x #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                int fail = 0;
                int localRandom = r.Next();
                do {
                    if ((localRandom % 100) < P) {
                        while (!Monitor.TryEnter(_clrLock)) {
                            fail++;
                            Thread.Sleep(10);
                        }
                        for (int i = 1; i < REENTER; i++) {
                            Monitor.Enter(_clrLock);
                        }
                        localRandom = sharedRandom = r.Next();
                        Platform.SpinWait(100);
                        for (int i = 1; i < REENTER; i++) {
                            Monitor.Exit(_clrLock);
                        }
                        Monitor.Exit(_clrLock);
                    } else {
                        localRandom = r.Next();
                    }
                    if ((++counts[id] % 50000) == 0) {
                        VConsole.Write("-{0}", id);
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ a/r #{0} exiting, after {1}[{2}] enter/exit",
                                    id, counts[id], fail);
                done.Signal();
            }
        }
        
        //
        // Starts the test.
        //
        
        internal static Action Run() {

            //System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
            for (int i = 0; i < THREADS; i++) {
                new EnterExit().Start(i, "a/r # " + i);
                Thread.Sleep(1);
            }
            int start = Environment.TickCount;
            Action stop = () => {
                shutdown.Set();
                int elapsed = Environment.TickCount - start;
                done.WaitOne();
                long total = 0;
                for (int i = 0; i < THREADS; i++) {
                    total += counts[i];
                }

                VConsole.WriteLine("+++ total: {0}, unit cost: {1} ns",
                                    total, (int)((elapsed * 1000000.0) / total));
            };
            return stop;
        }
	}
}
