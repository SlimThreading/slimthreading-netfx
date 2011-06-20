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
    static class TestRendezvousChannel {

        //
        // The number of threads.
        //

        private const int CLIENTS = 10;
        private const int SERVERS = 5;

        //
        // The rendezvous channel.
        //

        private static StRendezvousChannel<int, int> channel =
                                new StRendezvousChannel<int,int>(true);

        //
        // The alerter and the count down latch used for shutdown.
        //

        private static StAlerter shutdown = new StAlerter();
        private static StCountDownEvent done = new StCountDownEvent(CLIENTS + SERVERS);

        //
        // The counters. 
        //

        static int[] requests = new int[CLIENTS];
        static int[] services = new int[SERVERS];

        //
        // The send and wait reply client thread.
        //

        class SendAndWaitReplyClient {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ src #{0} started...", id);
                int request = id;
                int response;
                int fails = 0;
                do {
                    try {
                        while (!channel.SendWaitReply(request, out response,
                                                      new StCancelArgs(1, shutdown))) {
                            fails++;
                        }
                        if (response != request) {
                            VConsole.WriteLine("*** src #{0}: wrong response [{1}/{2}]",
                                              id, request, response);
                            break;
                        }
                        if ((++requests[id] % 10000) == 0) {
                            VConsole.Write("-src{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    if (++request < 0) {
                        request = id;
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ src #{0} exiting: [{1}/{2}]", id,
                                    requests[id], fails);
                done.Signal();
            }
        }
         
        //
        // The send only client thread.
        //

        class SendOnlyClient {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                VConsole.WriteLine("+++ soc #{0} started...", id);
                int request = id;
                int fails = 0;
                do {
                    try {
                        while (!channel.Send(request, new StCancelArgs(1, shutdown))) {
                            fails++;
                        }
                        if ((++requests[id] % 10000) == 0) {
                            VConsole.Write("-so{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    if (++request < 0) {
                        request = id;
                    }
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ soc #{0} exiting: [{1}/{2}]",
                                  id, requests[id], fails);
                done.Signal();
            }
        }

        //
        // The server thread.
        //

        class ServerThread {
            private int id;

            internal void Start(int tid, string tn) {
                id = tid;
                Thread t = new Thread(Run);
                t.Name = tn;
                t.Start();
            }

            private void Run() {
                int request;
                StRendezvousToken token;
                int fails = 0;
                VConsole.WriteLine("+++ svr #{0} started...", id);
                do {
                    try {
                        while (!channel.Receive(out request, out token,
                                                new StCancelArgs(1, shutdown))) {
                            fails++;
                        }
                        if (token.ExpectsReply) {
                            channel.Reply(token, request);
                        }
                        if ((++services[id] % 10000) == 0) {
                            VConsole.Write("-s{0}", id);
                        }
                    } catch (StThreadAlertedException) {
                        break;
                    }
                    //Thread.Sleep(1);
                } while (!shutdown.IsSet);
                VConsole.WriteLine("+++ svr #{0} exiting: [{1}/{2}]", id, services[id], fails);
                done.Signal();
            }
        }

        //
        // Starts the test.
        //

        internal static Action Run() {
        	for (int i = 0; i < SERVERS; i++) {
                new ServerThread().Start(i, "svr #" + i);
            }
	        for (int i = 0; i < CLIENTS; i++) {
                if (i < CLIENTS / 2) {
                    new SendAndWaitReplyClient().Start(i, "cwr #" + i);
                } else {
                    new SendOnlyClient().Start(i, "cso #" + i);
                }
	        }

            Action stop = () => {
                shutdown.Set();
                done.WaitOne();
        	    long rs = 0;
	            for (int i = 0; i < CLIENTS; i++) {
		            rs += requests[i];
	            }
        	    long srvs = 0;
	            for (int i = 0; i < SERVERS; i++) {
        		    srvs += services[i];
	            }

                VConsole.WriteLine("+++ Total: requests = {0}, services = {1}",
		                            rs, srvs);
            };
            return stop;
        }
    }
}
