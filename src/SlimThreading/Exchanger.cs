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

#pragma warning disable 0420

namespace SlimThreading {

	//
	// This class implements an exchange point, through which threads
    // can exchange generic data items.
	//

	public sealed class StExchanger<T> {

        //
        // The wait node used with the exchanger.
        //

        private sealed class WaitNode<Q> : StParker {
            internal Q channel;

            internal WaitNode(Q di) {
                channel = di;
            }
        }

		//
		// The exchange point.
		//

		private volatile WaitNode<T> xchgPoint;

        //
        // The number of spin cycles executed by a waiter thread
        // before it blocks on the park spot.
        //

        private readonly int spinCount;

        //
        // Constructors.
        //

        public StExchanger(int spinCount) {
            this.spinCount = Platform.IsMultiProcessor ? spinCount : 0;
        }

        public StExchanger() {}

		//
		// Exchanges a data item, activating the specified cancellers.
		//

		public bool Exchange(T mydi, out T yourdi, StCancelArgs cargs) {
            WaitNode<T> wn = null;
			do {

                WaitNode<T> you = xchgPoint;

				//
				// If there is thread waiting for exchange, get its wait node.
				//

                if (you != null) {
                    if (Interlocked.CompareExchange<WaitNode<T>>(ref xchgPoint, null, you) == you) {

                        //
                        // Try to lock the associated parker object;
                        // if succeed, exchange the data and unpark the waiter
                        // thread.
                        //

                        if (you.TryLock()) {
                            yourdi = you.channel;
                            you.channel = mydi;
                            you.Unpark(StParkStatus.Success);
                            return true;
                        }
                    }
                    continue;
                }

                //
                // No thread is waiting; if this is the first iteration check
                // if a null timeout was specified and, if so, return failure;
                // if not, create a wait node to signal our presence on the
                // exchange point.
                //

                if (wn == null) {
                    if (cargs.Timeout == 0) {
                        yourdi = default(T);
                        return false;
                    }
                    wn = new WaitNode<T>(mydi);
                }
                if (Interlocked.CompareExchange<WaitNode<T>>(ref xchgPoint, wn, null) == null) {
                    break;
                }
            } while (true);

            //
            // Park the current thread, activating the specified cancellers
            // and spinning if configured.
            //

            int ws = wn.Park(spinCount, cargs);

            //
            // If succeed, retrieve the data item from the wait node
            // and return success.
            //

            if (ws == StParkStatus.Success) {
                yourdi = wn.channel;
                return true;
            }

            //
            // The exchange was cancelled; so, try to remove our wait node
            // from the exchange point and report the failure appropriately.
            //

            if (xchgPoint == wn) {
                Interlocked.CompareExchange<WaitNode<T>>(ref xchgPoint, null, wn);
            }
            StCancelArgs.ThrowIfException(ws);
            yourdi = default(T);
            return false;
        }
	}
}
