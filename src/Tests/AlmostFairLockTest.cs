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
    public class AlmostFairLockTest {
        const int THREADS = 20;

        static readonly StAlmostFairLock @lock = new StAlmostFairLock (100);
        static readonly StAlerter shutdown = new StAlerter();
        static readonly StCountDownEvent done = new StCountDownEvent(THREADS);
        static readonly int[] counts = new int[THREADS];

        const int P = 25;

        static void Run(int id) {
            VConsole.WriteLine("+++ e/x #{0} started...", id);
            var r = new Random((id + 1) * Environment.TickCount);
            int fail = 0;
            int localRandom = r.Next();
            do {
                if ((localRandom % 100) < P) {
                    while (!@lock.Enter(new StCancelArgs(1))) {
                        fail++;
                    }
                    localRandom = r.Next();
                    Platform.SpinWait(100);
                    @lock.Exit();
                } else {
                    localRandom = r.Next();
                }
                if ((++counts[id] % 200000) == 0) {
                    VConsole.Write("-{0}", id);
                }
            } while (!shutdown.IsSet);
            VConsole.WriteLine("+++ a/r #{0} exiting, after {1}[{2}] enter/exit",
                                id, counts[id], fail);
            done.Signal();
        }

        public static Action Run() {
            for (int i = 0; i < THREADS; i++) {
                int id = i;
                new Thread(() => Run(id)) { Name = "e/x #" + id }.Start();
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

                VConsole.WriteLine("enter/exit: {0}, unit cost: {1} ns",
                                    total, (int)((elapsed * 1000000.0) / total));
            };
            return stop;
        }
    }
}
