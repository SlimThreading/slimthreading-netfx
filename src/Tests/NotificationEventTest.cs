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

	class NotificationEventTest {

        const int RING_THREADS = 16;
        const int GLOBAL_THREADS = 6;

        static readonly StAlerter shutdown = new StAlerter();
        static readonly StCountDownEvent done = new StCountDownEvent(RING_THREADS + GLOBAL_THREADS);

        static readonly int[] counts = new int[RING_THREADS];

        static readonly StCountDownEvent start = new StCountDownEvent(RING_THREADS);
        static readonly StNotificationEvent[] evts = new StNotificationEvent[RING_THREADS];

        public static void RingThread(int id) {
            int index = id;
            int count = 0;

            StWaitable.SignalAndWait(start, evts[index]);

            try { 
                do {
                    evts[index].Reset();
                    count++;
                    index = (index + 1) & (RING_THREADS - 1);
                    evts[index].Set();
                    evts[index].Wait(new StCancelArgs(shutdown));
                } while (!shutdown.IsSet);
            } catch (StThreadAlertedException) { }

            counts[index] = count;
            VConsole.WriteLine("+++ {0} exiting: {1}", Thread.CurrentThread.Name, count);
            done.Signal();
        }

        public static void GlobalThread(int id, bool waitAll) {
            int fail = 0;
            int count = 0;
            try {
                do {
                    if (waitAll ? StWaitable.WaitAll(evts, new StCancelArgs(shutdown))
                                : StWaitable.WaitAny(evts, new StCancelArgs(id, shutdown)) != StParkStatus.Timeout) {
                        count++;
                    } else {
                        fail++;
                    }
                } while (!shutdown.IsSet);
            }
            catch (StThreadAlertedException) { }

            VConsole.WriteLine("+++ {0} (Wait{1}) exiting: {2}/{3}", 
                               Thread.CurrentThread.Name, waitAll ? "All" : "Any", count, fail);
            done.Signal();    
         }

		public static Action Run() {
			for (int i = 0; i < RING_THREADS; i++) {
                int id = i;
			    evts[id] = new StNotificationEvent();
                new Thread(_ => RingThread(id)) { Name = "r #" + id }.Start();
			}

            for (int i = 0; i < GLOBAL_THREADS; i++) {
                int id = i;
                new Thread(_ => GlobalThread(id, (id & 1) == 0)) { Name = "g #" + id }.Start();
			}

		    start.Wait();
		    evts[0].Set();

            Action stop = () => {
                shutdown.Set();
                done.Wait();
                long t = 0;
                for (int i = 0; i <RING_THREADS; i++) {
                    t += counts[i];
                }

                VConsole.WriteLine("+++ Total: {0}", t);
            };
            return stop;
		}
	}
}
