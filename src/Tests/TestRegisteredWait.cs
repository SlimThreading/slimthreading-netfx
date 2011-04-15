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

    class TestRegisteredWait {


        private static int lastTime = Environment.TickCount;
        private static int count;

        //
        // Registered wait callback
        //

        private static void RegisteredWaitCallback(object state, bool timedOut) {
            StRegisteredWait regWait = (StRegisteredWait)state;

            int now = Environment.TickCount;
	        int elapsed = now - lastTime;
	        lastTime = now;

            if (timedOut) {
		        Console.WriteLine("+++Wait callback called due to timeout[{0}]", elapsed);
	        } else {
		        Console.WriteLine("+++Wait callback called due to success[{0}]", ++count);
                if (count == 25) {
                    regWait.Unregister();
                }
	        }
        }

        //
        // Unregister thread.
        //

        class UnregisterThread {
        	StNotificationEvent doUnreg;
	        StRegisteredWait regWait;
	
	        public UnregisterThread(StRegisteredWait regWait, StNotificationEvent doUnreg) {
                this.regWait = regWait;
                this.doUnreg = doUnreg;
            }

            public void Start() {
                new Thread(Run).Start();
            }

            private void Run() {
	            Console.WriteLine("+++unregister thread starts...");
                doUnreg.Wait();
                if (regWait.Unregister()) {
                    Console.WriteLine("+++ WAIT successful unregistered");
                } else {
                    Console.WriteLine("+++ Unregister WAIT FAILED!");
                }
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {

            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            StSemaphore s = new StSemaphore(0);
            StNotificationEvent doUnreg = new StNotificationEvent();
        	StRegisteredWait regWait = s.RegisterWait(RegisteredWaitCallback, null, 250, false);
            new UnregisterThread(regWait, doUnreg).Start();
	        s.Release(20);
            return () => {
	            doUnreg.Set();
	            for (int i = 0; i < 100; i++) {
		            s.Release(1);
	            }
	            Console.ReadLine();
            };
        }
    }
}
