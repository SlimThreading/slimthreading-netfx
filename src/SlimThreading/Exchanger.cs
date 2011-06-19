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

#pragma warning disable 0420

namespace SlimThreading {

    //
    // The delegate type used with registered exchanges.
    //

    public delegate void ExchangeCallback<in T>(object state, T dataItem, bool timedOut);

    //
    // Represents the registration of a callback for asynchronous data exchange.
    //

    public struct ExchangeRegistration {
        private StParker parker;

        public ExchangeRegistration(StParker parker) {
            this.parker = parker;
        }

        //
        // Tries to unregister the callback. This method is thread-safe.
        //

        public bool Unregister() {
            StParker p = parker;
            if (p == null) {
                return false;
            }
            
            parker = null;
            
            if (p.TryCancel()) {
                p.Unpark(StParkStatus.WaitCancelled);
                return true;
            }
            return false;
        }
    }

	//
	// This class implements an exchange point, through which threads
    // can exchange generic data items.
	//

	public sealed class StExchanger<T> {

        //
        // The wait node used with the exchanger.
        //

        internal sealed class WaitNode {
            internal readonly StParker Parker;
            internal T Channel;

            internal WaitNode(StParker parker, T dataItem) {
                Parker = parker;
                Channel = dataItem;
            }
        }

		internal volatile WaitNode xchgPoint;
        private readonly int spinCount;

        public StExchanger(int spinCount) {
            this.spinCount = Platform.IsMultiProcessor ? spinCount : 0;
        }

        public StExchanger() { }

        public static bool ExchangeAny(StExchanger<T>[] xchgs, T myData, out T yourData, StCancelArgs cargs) {
            int len = xchgs.Length;

            for (int i = 0; i < len; i++) {
                if (xchgs[i].TryExchange(myData, out yourData)) {
                    return true;
                }
            }

            yourData = default(T);

            if (cargs.Timeout == 0) {
                return false;
            }

            var pk = new StParker(1);
            var wn = new WaitNode(pk, myData);
            int lv = -1;
            int gsc = 0;

            for (int i = 0; !pk.IsLocked && i < len; i++) {
                StExchanger<T> xchg = xchgs[i];

                if (xchg.TryExchange(wn, myData, out yourData)) {
                    break;
                }

                //
                // Adjust the global spin count.
                //

                int sc = xchg.spinCount;
                if (gsc < sc) {
                    gsc = sc;
                }
                
                lv = i;
            }
            
            int wst = pk.Park(gsc, cargs);
            
            for (int i = 0; i <= lv; i++) {
                xchgs[i].CancelExchange(wn);
            }

            if (wst == StParkStatus.Success) {
                return true;
            }

            StCancelArgs.ThrowIfException(wst);
            return false;
        }

		//
		// Exchanges a data item, activating the specified cancellers.
		//

		public bool Exchange(T myData, out T yourData, StCancelArgs cargs) {
            if (TryExchange(myData, out yourData)) {
                return true;
            }

		    var pk = new StParker();
            var wn = new WaitNode(pk, myData);
            if (TryExchange(wn, myData, out yourData)) {
                return true;
            }

            int ws = pk.Park(spinCount, cargs);
            if (ws == StParkStatus.Success) {
                yourData = wn.Channel;
                return true;
            }

            CancelExchange(wn);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

	    //
        // Exchanges a data item asynchronously. The specified callback gets called
        // when the exchange completes and is passed the received data item.
        //      
        //  TODO: Prevent unbounded reentrancy (hacked by queueing to the pool).
        //

        public ExchangeRegistration RegisterExchange(T myData, ExchangeCallback<T> callback, 
                                                     object state, int timeout) {
            if (timeout == 0) {
                throw new ArgumentOutOfRangeException("timeout", "The timeout can't be zero");
            }
            if (callback == null) {
                throw new ArgumentNullException("callback");
            }

            T yourData;
            if (TryExchange(myData, out yourData)) {
                ThreadPool.QueueUserWorkItem(_ => callback(state, yourData, false));
                return new ExchangeRegistration();
            }
            
            state = state ?? this;
            WaitNode wn = null;
            var cbparker = new CbParker(ws => {
                if (ws != StParkStatus.Success && xchgPoint == wn) {
                    Interlocked.CompareExchange(ref xchgPoint, null, wn);
                }
                        
                if (ws != StParkStatus.WaitCancelled) {
                    callback(state, wn.Channel, ws == StParkStatus.Timeout);
                }
            });
            wn = new WaitNode(cbparker, myData);

            if (TryExchange(wn, myData, out yourData)) {
                ThreadPool.QueueUserWorkItem(_ => callback(state, yourData, false));
                return new ExchangeRegistration();                
            }

            var timer = timeout != Timeout.Infinite ? new RawTimer(cbparker) : null;
            int waitStatus = cbparker.EnableCallback(timeout, timer);
            
            if (waitStatus != StParkStatus.Pending) {
                ThreadPool.QueueUserWorkItem(_ => callback(state, wn.Channel, waitStatus == StParkStatus.Timeout));
                return new ExchangeRegistration();
            }
            return new ExchangeRegistration(cbparker);
        }
        
       private bool TryExchange(T myData, out T yourData) {
            WaitNode you;

            //
            // If there's a waiter, try to lock the associated parker object 
            // and unpark the waiter.
            //
            
            if (xchgPoint != null && 
                (you = Interlocked.Exchange(ref xchgPoint, null)) != null
                && you.Parker.TryLock()) {

                yourData = you.Channel;
                you.Channel = myData;
                you.Parker.Unpark(StParkStatus.Success);
                return true;
            }

            yourData = default(T);
            return false;
        }

        private bool TryExchange(WaitNode wn, T myData, out T yourData) {
			do {
                if (TryExchange(myData, out yourData)) {
                    return true;
                }

                if (Interlocked.CompareExchange(ref xchgPoint, wn, null) == null) {
                    return false;
                }
            } while (true);
        }

        private void CancelExchange(WaitNode wn) {
            
            //
            // The exchange was cancelled; so, try to remove our wait node
            // from the exchange point and report the failure appropriately.
            //

	        if (xchgPoint == wn) {
	            Interlocked.CompareExchange(ref xchgPoint, null, wn);
	        }
	    }
	}
}