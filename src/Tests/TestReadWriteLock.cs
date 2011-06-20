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
	internal class TestReadWriteLock {

        //
        // The number of threads.
        //

		private const int READERS = 4;
		private const int WRITERS = 2;

        //
        // The read write lock.
        //

		private static StReadWriteLock rwl =  new StReadWriteLock(100);

        //
        // The alerter and the count down event used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(READERS + WRITERS);

        //
        // The counters.
        //

        static int[] reads = new int[READERS];
		static int[] writes = new int[WRITERS];

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
                do {
                    try {
                        if (rwl.TryEnterRead(new StCancelArgs(r.Next(10) + 1, shutdown))) {
                            if ((++reads[id] % 20000) == 0) {
                                VConsole.Write("-r{0}", id);
                            }
                            rwl.ExitRead();
                        } else {
                            fail++;
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ rd #{0} exiting: [{1}/{2}]", id, reads[id], fail);
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
                do {
                    try {
                        if (rwl.TryEnterWrite(new StCancelArgs(r.Next(10) + 1, shutdown))) {
                            if ((++writes[id] % 20000) == 0) {
                                VConsole.Write("-w{0}", id);
                            }
                            rwl.ExitWrite();
                        } else {
                            fail++;
                        }
                        //Thread.Sleep(r.Next(1, 10));
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ wr #{0} exiting: [{1}/{2}]", id, writes[id], fail);
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

            Action stop = () => {
                shutdown.Set();
                done.WaitOne();
			    long rds = 0, wrs = 0;
			    for (int i = 0; i < READERS; i++) {
    				rds += reads[i];
			    }
			    for (int i = 0; i < WRITERS; i++) {
				    wrs += writes[i];
			    }
				VConsole.WriteLine("+++ Total: reads = {0}, writes = {1}", rds, wrs);
            };
            return stop;
		}
	}
}
