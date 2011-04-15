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
    class TestSemaphores {
        
        //
        // The number of threads.
        //

        private const int RELEASERS = 5;
        private const int ACQUIRERS = 10;

        //
        // The semaphores.
        //

        private const int SEMAPHORES = 100;
        static StSemaphore[] ss = new StSemaphore[SEMAPHORES];

        //
        // The alerter and the count latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done =
                                new StCountDownEvent(ACQUIRERS + RELEASERS);

        //
        // The counters.
        //

        static int[] acquires = new int[ACQUIRERS];
        static int[] releases = new int[RELEASERS];

        //
        // The releaser thread.
        //

        class Releaser {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Start();
                t.Name = tn;
            }

            private void Run() {
                VConsole.WriteLine("+++ r #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                do {
                    try {
                        ss[r.Next() % SEMAPHORES].Release(1);
                    } catch (StSemaphoreFullException) {
                        StParker.Sleep(new StCancelArgs(10));
                    }
                    releases[id]++;
                    Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ releaser #{0} exiting, [{1}]", id, releases[id]);
                done.Signal();
            }
        }

        //
        // The acquirer thread.
        //

        class Acquirer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ a #{0} started...", id);
                int fail = 0;
                int index;
                do {
                    try {
                        index = StWaitable.WaitAny(ss, new StCancelArgs(1, shutdown));
                        if (index >= 0 && index < SEMAPHORES) {
                            if ((++acquires[id] % 500) == 0) {
                                VConsole.Write("-{0}", id);
                            }
                        } else {
                            fail++;
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    //Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ a #{0} exiting, [{1}/{2}]", id, acquires[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < SEMAPHORES; i++) {
                ss[i] = new StSemaphore(0);
            }
            for (int i = 0; i < ACQUIRERS; i++) {
                new Acquirer().Start(i, "a #" + i);
            }
            for (int i = 0; i < RELEASERS; i++) {
                new Releaser().Start(i, "r #" + i);
            }

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                long rels = 0;
                for (int i = 0; i < RELEASERS; i++) {
                    rels += releases[i];
                }
                long acqs = 0;
                for (int i = 0; i < ACQUIRERS; i++) {
                    acqs += acquires[i];
                }

                VConsole.WriteLine("+++ Total: rels = {0}, acqs = {1}", rels, acqs);
            };
            return stop;
        }
    }
}
