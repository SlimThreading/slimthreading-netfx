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
	class TestExchanger {

        //
        // The number of threads.
        //

		private const int EXCHANGERS = 10;

        //
        // The exchanger.
        //

		private static StExchanger<int> xchg = new StExchanger<int>(100);

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(EXCHANGERS);

        //
        // The counters.
        //

        static int[] counts = new int[EXCHANGERS];

        //
        // The exchanger thread.
        //

        class ExchangerThread {
            private int id;

            internal void Start(int tid, string name) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = name;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ x #{0} started...", id);
                Random rand = new Random(id);
                int fail = 0;
                do {
                    try {
                        int yourId;
                        while (!xchg.Exchange(id, out yourId, new StCancelArgs(1, shutdown))) {
                            fail++;
                        }
                        if ((++counts[id] % 1000) == 0) {
                            VConsole.Write("-{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                } while (true);
                VConsole.WriteLine("+++ x #{0} exiting: [{1}/{2}]", id, counts[id], fail);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

		internal static Action Run() {
			for (int i = 0; i < EXCHANGERS; i++) {
			    new ExchangerThread().Start(i, "x #" + i);
			}

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                long xs = 0;
                for (int i = 0; i < EXCHANGERS; i++) {
                    xs += counts[i];
                }
                VConsole.WriteLine("---Total: {0}", xs);
            };
            return stop;
		}
	}
}
