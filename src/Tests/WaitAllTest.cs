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
    public class WaitAllTest {
        private const int THREADS = 10;

        private static volatile int shutdown;
        private static readonly StCountDownEvent done = new StCountDownEvent(THREADS);

        private static readonly int[] counts = new int[THREADS];

        private static void Run(int id) {
            int fail = 0;
            var sem0 = new StSemaphore(0);
            var sem1 = new StSemaphore(0);
            var sem2 = new Semaphore(0, int.MaxValue);
            var sem3 = new Semaphore(0, int.MaxValue);
            var stsems = new[] { sem0, sem1 };
            var whsems = new[] { sem2, sem3 };

            VConsole.WriteLine("+++ w #{0} started...", id);
            do {
                ThreadPool.QueueUserWorkItem(delegate {
                    sem0.Release(1);
                    sem1.Release(1);
                    Thread.Sleep(0);
                    sem2.Release(1);
                    sem3.Release(1);
                    if (sem1.Wait(1, new StCancelArgs(0))) { 
                        Thread.Sleep(0);
                        sem1.Release(1);
                    }
                });

                try {
                    do { 
                        if (StWaitable.WaitAll(stsems, whsems, new StCancelArgs(id))) {
                            break;
                        }
                        fail++;
                    } while (true);
                } catch (StThreadAlertedException) {
                    break;
                }

                if ((++counts[id] % 1000) == 0) {
                    VConsole.Write("-{0}", id);
                }
            } while (shutdown == 0);
            VConsole.WriteLine("+++ w #{0} exiting: [{1}/{2}]", id, counts[id], fail);
            done.Signal();
        }

        internal static Action Run() {
            for (int i = 0; i < THREADS; i++) {
                int id = i;
                new Thread(() => Run(id)) { Name = "w #" + id }.Start();
            }

            Action stop = () => {
                shutdown = 1;
                done.Wait();
                long t = 0;
                for (int i = 0; i < THREADS; i++) {
                    t += counts[i];
                }

                VConsole.WriteLine("+++ Total: {0}", t);
            };
            return stop;
        }
    }
}
