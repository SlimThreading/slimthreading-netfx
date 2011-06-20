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
using System.Diagnostics;
using SlimThreading;

namespace TestShared {
	class TestSemaphore {

        //
        // The number of threads.
        //

		private const int RELEASERS = 5;
		private const int ACQUIRERS = 10;

        //
        // The semaphore.
        //

		static StSemaphore s = new StSemaphore(0);

        //
        // The alerter and the count event used for shutdown.
        //

		static StAlerter shutdown = new StAlerter();
        static StCountDownEvent done = new StCountDownEvent(ACQUIRERS + RELEASERS);

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
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ r #{0} started...", id);
                do {
                    s.Release(1);
                    releases[id]++;
                    Thread.Sleep(0);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ r #{0} exiting: [{1}]", id, releases[id]);
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
                int fail = 0;
                StWaitable[] ws = new StWaitable[] { s };
                VConsole.WriteLine("+++ a #{0} started...", id);
                do {
                    try {
                        while (!s.Wait(1, new StCancelArgs((id & 1) + 1, shutdown))) {
                            //while (!s.Wait(new StCancelArgs((n & 1) + 1, shutdown))) {
                            //while (StWaitable.WaitAny(ws, new StCancelArgs((n & 1) + 1, shutdown)) == StParkStatus.Timeout) {
                            //while (!StWaitable.WaitAll(ws, new StCancelArgs((n & 1) + 10, shutdown))) {
                            if (shutdown.IsSet) {
                                goto Exit;
                            }
                            fail++;
                            //Thread.Sleep(0);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    if ((++acquires[id] % 20000) == 0) {
                        VConsole.Write("-{0}", id);
                    }
                } while (!shutdown.IsSet);
            Exit:
                VConsole.WriteLine("+++ acquirer #{0} exiting: [{1}/{2}]",
                                    id, acquires[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
            for (int i = 0; i < ACQUIRERS; i++) {
                new Acquirer().Start(i, "a #" + i);
            }
            for (int i = 0; i < RELEASERS; i++) {
                new Releaser().Start(i, "r #" + i);
            }
            Action stop = () => {
                shutdown.Set();
                done.WaitOne();
                long rels = 0;
                for (int i = 0; i < RELEASERS; i++) {
                    rels += releases[i];
                }
                long acqs = 0;
                for (int i = 0; i < ACQUIRERS; i++) {
                    acqs += acquires[i];
                }
                VConsole.WriteLine("+++ Total: rel = {0}, acqs = {1}", rels, acqs);
            };
            return stop;
        }
	}
}
