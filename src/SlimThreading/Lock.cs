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
    // This class implements a non-reentrant non-fair lock.
    //

    public sealed class StLock : IMonitorLock {

        //
        // Request type used for normal lock acquire requests.
        //

        private const int ACQUIRE = 1;
        private const int LOCKED_ACQUIRE = (WaitBlock.LOCKED_REQUEST | ACQUIRE);

        //
		// The lock state.
		//

		internal const int FREE = 0;
        internal const int BUSY = 1;
        internal volatile int state;
 
		//
		// The lock's wait queue that is a non-blocking stack.
		//

		private volatile WaitBlock top;	

		//
		// The number of spin cycles executed before a thread inserts
        // a wait block in the lock's queue and blocks.
		//

		private int spinCount;

        //
        // Constructors.
        //

        public StLock(int sc) {
            spinCount = Platform.IsMultiProcessor ? sc : 0;
        }

        public StLock() {}

		//
		// Tries to acquire the lock immediately.
		//

		public bool TryEnter() {
            return (state == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE);
		}

        //
        // Acquires the lock unconditionally.
        //

        public void Enter() {
            if (state == FREE &&
                Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                return;
            }
            SlowEnter();
        }

        //
        // Tries to acquires a busy lock.
        //

        private void SlowEnter() {
            WaitBlock wb = null;
            do {

                //
                // First, try to acquire the lock spinning for the configured
                // number of cycles, but only if the wait queue is empty.
                //

                int sc = spinCount;
                do {
                    if (state == FREE &&
                        Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                        return;
                    }
                    if (top != null || sc-- <= 0) {
                        break;
                    }
                    Platform.SpinWait(1);
                } while (true);

                //
                // The lock is busy; so create a locked wait block, or
                // reset the one previously allocated, and insert it in
                // the wait queue.
                //

                if (wb == null) {
                    wb = new WaitBlock(new StParker(0), LOCKED_ACQUIRE);
                } else {
                    wb.parker.Reset(0);
                }
                do {
                    WaitBlock t;
                    wb.next = (t = top);
                    if (Interlocked.CompareExchange<WaitBlock>(ref top, wb, t) == t) {
                        break;
                    }
                } while (true);

                //
                // Since that the lock can become free after we inserted
                // the wait block, we must try to acquire it again, if it
                // seems free.
                //
 
                if (state == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                    return;
                }

                //
                // Park the current thread before and, after it is release,
                // retry to acquire the lock.
                //

                wb.parker.Park();
            } while (true);
        }

        //
        // Tries to acquire the lock, activating the specified
        // cancellers.
        //

        public bool TryEnter(StCancelArgs cargs) {
            if (state == FREE &&
                Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                return true;
            }
            return (cargs.Timeout != 0) ? SlowTryEnter(cargs) : false;
        }

        //
        // Tries to acquire a busy lock, activating the specified cancellers.
        //

        private bool SlowTryEnter(StCancelArgs cargs) {

	        //
	        // If a timeout was specified, get a time reference in order
            // to adjust the timeout value if the thread need to re-wait.
	        //

	        int lastTime = (cargs.Timeout != Timeout.Infinite) ? Environment.TickCount : 0;
            WaitBlock wb = null;
            do {
		        //
		        // First, try to acquire the lock spinning for the configured
                // number of cycles, but only if the wait queue is empty.
		        //
		    
                int sc = spinCount;
		        do {
			        if (state == FREE &&
                        Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                        return true;
                    }
                    if (top != null || sc-- <= 0) {
                        break;
                    }
                    Platform.SpinWait(1);
                } while (true);

                //
		        // The lock is busy; so, create a wait block or reset the
                // one previously created and insert it in the wait queue.
		        //

                if (wb == null) {
                    wb = new WaitBlock(ACQUIRE);
                } else {
                    wb.parker.Reset();
                }
                do {
                    WaitBlock t;
                    wb.next = (t = top);
                    if (Interlocked.CompareExchange<WaitBlock>(ref top, wb, t) == t) {
                        break;
                    }
                } while (true);

                //
                // Since that the lock can become free after we inserted
                // the wait block, we must retry to acquire the lock, if it
                // seems free.
                //

                if (state == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                    return true;
                }

                //
                // Park the current thread, activating the specified cancellers.
                //

                int ws = wb.parker.Park(cargs);

                //
                // If the acquire attempt was cancelled; so, report the
                // failure appropriately.
                //

                if (ws != StParkStatus.Success) {
                    StCancelArgs.ThrowIfException(ws);
                    return false;
                }

                //
                // Before adjust the timeout value, try to acquire the lock.
                //

			    if (state == FREE &&
                    Interlocked.CompareExchange(ref state, BUSY, FREE) == FREE) {
                    return true;
                }
            
		        //
		        // If a timeout was specified, adjust its value taking into
                // account the elapsed time.
		        //

		        if (cargs.Timeout != Timeout.Infinite) {
			        int now = Environment.TickCount;
			        int e = (now == lastTime) ? 1 : (now - lastTime);
			        if (cargs.Timeout <= e) {
				        return false;
			        }
			        cargs.Timeout -= e;
			        lastTime = now;
		        }
            } while (true);
        }
	
		//
		// Exits the lock.
		//

		public void Exit() {

            //
            // Since that atomic operations on references are more expensive
            // than on integers, we try to optimize the release when the wait
            // queue empty. However, when when the wait queue is empty before
            // the lock is released, but it's seen non-empty after the lock is
            // released, our algorithm resorts to two atomic instructions.
            //

            if (top == null) {
                Interlocked.Exchange(ref state, FREE);
                if (top == null) {
                    return;
                }
            } else {
                state = FREE;
            }

            //
            // Unpark all waiting threads.
            //
            // NOTE: Since that the spin lock's queue is implemented with
            //       a stack, we build another stack in order to unpark the
            //       waiting thread according to its arrival order.
            //
            
            WaitBlock p = Interlocked.Exchange<WaitBlock>(ref top, null);
            WaitBlock ws = null, n;
            while (p != null) {
                n = p.next;
                if (p.request < 0 || p.parker.TryLock()) {
                    p.next = ws;
                    ws = p;
                }
                p = n;
            }
            while (ws != null) {
                n = ws.next;
                ws.parker.Unpark(StParkStatus.Success);
                ws = n;
            }
		}

        /*++
         * 
         * IMonitorLock interface implementation.
         * 
         --*/

        //
        // Returns true if the lock is busy.
        //

        bool IMonitorLock.IsOwned {
            get { return state == BUSY; }
        }

        //
        // Exits the lock.
        //

        int IMonitorLock.ExitCompletely() {
            Exit();
            return 1;
        }

        //
        // Aquires the lock.
        //

        void IMonitorLock.Reenter(int ignored, int ignored2) {
            Enter();
        }

        //
        // Enqueues the specified wait block in the lock's queue.
        //
        // NOTE: When this method is called, the lock is owned by
        //       the current thread.
        //

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            wb.request = LOCKED_ACQUIRE;
            do {
                WaitBlock t;
                wb.next = (t = top);
                if (Interlocked.CompareExchange<WaitBlock>(ref top, wb, t) == t) {
                    return;
                }
            } while (true);
        }
	}
}
