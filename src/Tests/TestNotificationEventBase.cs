﻿// Copyright 2011 Carlos Martins
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

    //
    // A custom manual-reset event.
    //

    public sealed class MyEvent : StNotificationEventBase {

        public MyEvent(bool initialState, int sc) : base(initialState, sc) {}

        public MyEvent() : base(false, 0) {}

        public bool Set() {
            return InternalSet();
        }

        public bool Wait(StCancelArgs cargs) {
            int ws = InternalWait(cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }
            if (ws == StParkStatus.Alerted) {
                throw new StThreadAlertedException();
            }
            /*
            if (ws == StParkStatus.Interrupted) {
                throw new ThreadInterruptedException();
            }
             */
            return false;
        }

        public bool Reset() {
            return InternalReset();
        }

        public bool IsSet { get { return InternalIsSet; } }
    }

	class TestNotificationEventBase {

        //
        // The number of worker threads.
        //

		private const int THREADS = 20;

        //
        // The alerter and the count down event used for shutdown.
        //

        static StAlerter shutdown = new StAlerter();
        static StCountDownEvent done = new StCountDownEvent(THREADS);

        //
        // The counters.
        //

		static int[] counts = new int[THREADS];
    
        //
        // The worker thread.
        //

        class Worker {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                int fail = 0;

                VConsole.WriteLine("+++ w #{0} started...", id);
                do {
                    MyEvent mre = new MyEvent();
                    MyEvent mre2 = new MyEvent();
                    MyEvent mre3 = new MyEvent();
                    MyEvent mre4 = new MyEvent();
                    StNotificationEventBase[] mres = new StNotificationEventBase[] { mre, mre2, mre3, mre4 };
                    ThreadPool.QueueUserWorkItem(delegate(object ignored) {
                        mre.Set();
                        mre2.Set();
                        Thread.Sleep(0);
                        mre3.Set();
                        mre4.Set();
                    });
                    try {
                        do {
                            if (StNotificationEventBase.WaitAll(mres,
                                                 new StCancelArgs((id & 1) + 1, shutdown))) {
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
                } while (true);
                VConsole.WriteLine("+++ w #{0} exiting: [{1}/{2}]", id, counts[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

		internal static Action Run() {
			for (int i = 0; i < THREADS; i++) {
				new Worker().Start(i, "w #" + i);
			}

            Action stop = () => {
                shutdown.Set();
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
