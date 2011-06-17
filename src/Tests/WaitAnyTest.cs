// Copyright 2011 Carlos Martins, Duarte Nunes
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

namespace Tests {
    public class WaitAnyTest {

        private const int RELEASERS = 5;
        private const int ACQUIRERS = 10;

        private const int SEMAPHORES = 100;
        private static readonly StSemaphore[] sems = new StSemaphore[SEMAPHORES / 2];
        private static readonly Semaphore[] handles = new Semaphore[SEMAPHORES / 2];

        private static readonly StAlerter shutdown = new StAlerter();
        private static readonly StCountDownEvent done = new StCountDownEvent(ACQUIRERS + RELEASERS);
        
        private static readonly int[] acquires = new int[ACQUIRERS];
        private static readonly int[] releases = new int[RELEASERS];

        class Releaser {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                new Thread(Run) { Name = tn }.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ r #{0} started...", id);
                Random r = new Random((id + 1) * Environment.TickCount);
                do {
                    try {
                        int idx = r.Next() % SEMAPHORES;
                        if (idx < sems.Length) {
                            sems[idx].Release(1);
                        } else {
                            handles[idx - sems.Length].Release(1);
                        }
                        releases[id]++;
                    } catch (StSemaphoreFullException) {
                        StParker.Sleep(new StCancelArgs(10));
                    }
                    Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ releaser #{0} exiting, [{1}]", id, releases[id]);
                done.Signal();
            }
        }
        
        class Acquirer {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                new Thread(Run) { Name = tn }.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ a #{0} started...", id);
                int fail = 0;
                do {
                    try {
                        int index = StWaitable.WaitAny(sems, handles, new StCancelArgs(1, shutdown));
                        if (index >= 0 && index < SEMAPHORES) {
                            if ((++acquires[id] % 500) == 0) {
                                VConsole.Write("-{0}", id);
                            }
                        } else {
                            fail++;
                        }
                    }
                    catch (StThreadAlertedException) {
                        break;
                    }
                    //Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ a #{0} exiting, [{1}/{2}]", id, acquires[id], fail);
                done.Signal();
            }
        }

        internal static Action Run() {
            for (int i = 0; i < sems.Length; i++) {
                sems[i] = new StSemaphore(0);
            }

            for (int i = 0; i < handles.Length; i++) {
                handles[i] = new Semaphore(0, int.MaxValue);
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

                foreach (StSemaphore sem in sems) {
                    while (sem.Wait(1, new StCancelArgs(0))) {
                        acqs += 1;
                    }
                }

                foreach (Semaphore handle in handles) {
                    while (handle.WaitOne(0)) {
                        acqs += 1;
                    }
                }

                Assert.AreEqual(rels, acqs);
            };
            return stop;
        }
    }
}
