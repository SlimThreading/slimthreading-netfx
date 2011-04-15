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
	// This class implements a semaphore.
	//

	public sealed class StSemaphore : StWaitable {

		//
		// The semaphore's state and the wait queue.
		//

        private volatile int state;
        private LockedWaitQueue queue;

        //
        // The maximum number of available permits.
        //

        private int maximumCount;
	
        //
        // The number of spin cycles executed before block the
        // first waiter thread on the semaphore.
        //

        private int spinCount;

		//
		// Constructors.
		//

		public StSemaphore(int count, int maximumCount, int spinCount) {
            if (count < 0 || count > maximumCount) {
                throw new ArgumentException("\"count\": incorrect value");
            }
            if (maximumCount <= 0) {
                throw new ArgumentException("\"maximumCount\": incorrect value");
            }
			queue.Init();
			state = count;
            this.maximumCount = maximumCount;
            this.spinCount = Platform.IsMultiProcessor ? spinCount : 0;
		}

        public StSemaphore(int count, int maximumCount) : this(count, maximumCount, 0) {}

		public StSemaphore(int count) : this(count, Int32.MaxValue, 0) {}

        //
		// Tries to acquire the specified number of permits on behalf
        // of the current thread.
		// 

		private bool TryAcquireInternal(int acquireCount) {
			do {
                int s;
				int ns = (s = state) - acquireCount;
                if (ns < 0 || !queue.IsEmpty) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, ns, s) == s) {
                    return true;
                }
			} while (true);
		}

		//
		// Tries to acquire the specified number of permits on behalf
		// of the thread that is at the front of the semaphore's queue.
		// 

		private bool TryAcquireInternalQueued(int acquireCount) {
			do {
                int s;
                int ns = (s = state) - acquireCount;
                if (ns < 0) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, ns, s) == s) {
                    return true;
                }
			} while (true);
		}

        //
        // Undoes a previous acquire.
        //

        private void UndoAcquire(int undoCount) {
            do {
                int s = state;
                if (Interlocked.CompareExchange(ref state, s + undoCount, s) == s) {
                    return;
                }
            } while (true);
        }

		//
		// Releases the specified number of permits.
		//

		private bool ReleaseInternal(int releaseCount) {
            do {
                int s;
                int ns = (s = state) + releaseCount;
                if (ns < 0 || ns > maximumCount) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, ns, s) == s) {
                    return true;
                }
            } while (true);
		}

        //
        // If at least a waiter can be released, returns true with the
        // semaphore's queue locked; otherwise, returns false.
        //

        private bool IsReleasePending {
            get {
                WaitBlock w = queue.First;
                return (w != null && (state >= w.request || w.parker.IsLocked) && queue.TryLock());
            }
        }

        //
		// Releases the appropriate waiters and unlocks the semaphore's queue.
        //

		private void ReleaseWaitersAndUnlockQueue() {
			do {
				WaitBlock qh = queue.head;
                WaitBlock w;
                while (state > 0 && (w = qh.next) != null) {
                    StParker pk = w.parker;
                    if (w.waitType == WaitType.WaitAny) {

                        //
                        // Try to acquire the requested permits on behalf of the
                        // queued waiter.
                        //

                        if (!TryAcquireInternalQueued(w.request)) {
                            break;
                        }

                        //
                        // Try to lock the associated parker and, if succeed, unpark
                        // its owner thread.
                        //

                        if (pk.TryLock()) {
                            pk.Unpark(w.waitKey);
                        } else {

                            //
                            // The acquire attempt was cancelled, so undo the
                            // previous acquire.
                            //  

                            UndoAcquire(w.request);
                        }
                    } else {

                        //
                        // Wait-all: since that the semaphore seems to have at least
                        // one available permit, lock the parker and, if this is the last
                        // cooperative release, unpark its owner thread.
                        //

                        if (pk.TryLock()) {
                            pk.Unpark(w.waitKey);
                        }
                    }

                    //
                    // Remove the wait block from the semaphore's queue,
                    // marking as unlink the previous heas, and advance the
                    // local queues's head.
                    //

                    qh.next = qh;
                    qh = w;
                }

				//
				// It seems that no more waiters can be released; so,
                // set the new semaphore queue's head and unlock it.
                //

				queue.SetHeadAndUnlock(qh);

                //
                // If, after the semaphore's queue is unlocked, it seems
                // that more waiters can be released, repeat the release
                // processing.
                //

                if (!IsReleasePending) {
                    return;
                }
			} while (true);
		}

		//
		// Cancels the specified acquire attempt.
		//

        private void CancelAcquire(WaitBlock wb) {

            //
            // If the wait block is still linked and it isn't the last wait block
            // of the queue and the queue's lock is free unlink the wait block.
            //

            WaitBlock wbn;
            if ((wbn = wb.next) != wb && wbn != null && queue.TryLock()) {
                queue.Unlink(wb);
                ReleaseWaitersAndUnlockQueue();
            }
        }

        //
        // Enqueues an acquire.
        //

        private int EnqueueAcquire(WaitBlock wb, int acquireCount) {

            //
            // Enqueue the wait block in the semaphore's queue.
            //

            bool isFirst = queue.Enqueue(wb);

            //
            // if the wait block was inserted at front of the semaphore's
            // queue, re-check if the current thread is at front of the
            // queue and can now acquire the requested permits; if so,
            // try lock the queue and execute the release processing.
            //

            if (isFirst && state >= acquireCount && queue.TryLock()) {
                ReleaseWaitersAndUnlockQueue();
            }

            //
            // Return the number of spin cycles.
            //

            return (isFirst ? spinCount : 0);
        }

		//
		// Waits until acquire the specified number of permits, activating
        // the specified cancellers.
        //

		public bool Wait(int acquireCount, StCancelArgs cargs) {

            //
            // Validate the "acquireCount" argument.
            //

            if (acquireCount <= 0 || acquireCount > maximumCount) {
                throw new ArgumentException("acquireCount");
            }

			//
            // Try to acquire the specified permits and, if succeed,
            // return success.
			//

            if (TryAcquireInternal(acquireCount)) {
                return true;
            }

			//
			// Return failure if a null timeout was specified.
			//

            if (cargs.Timeout == 0) {
                return false;
            }

			//
			// Create a wait block and insert it in the semaphore's queue.
            //

            WaitBlock wb = new WaitBlock(WaitType.WaitAny, acquireCount);
            int sc = EnqueueAcquire(wb, acquireCount);

            //
			// Park the current thread, activating the specified cancellers and
            // spinning, if appropriate.
			//

			int ws = wb.parker.Park(sc, cargs);

            //
            // if we acquired the requested permits, return success.
            //

            if (ws == StParkStatus.Success) {
                return true;
            }

			//
			// The request was cancelled; so, cancel the acquire attempt
            // and report the failure appropriately.
			//

			CancelAcquire(wb);
            StCancelArgs.ThrowIfException(ws);
            return false;
		}

		//
		// Waits unconditionally until acquire the specified number
        // of permits.
        //

		public void Wait(int acount) {
		    Wait(acount, StCancelArgs.None);
        }
        
        //
		// Releases the specified number permits.
		//

		public int Release(int rcount) {

            if (rcount < 1) {
                throw new ArgumentOutOfRangeException("\"rcount\" must be positive");
            }

			//
			// Upadate the semaphore's state and if there are waiters
            // that can be released, execute the release processing.
			//

            int prevCount = state;
            if (!ReleaseInternal(rcount)) {
                throw new StSemaphoreFullException();
            }
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue();
            }
            return prevCount;
		}
		
		/*++
		 * 
		 * Virtual methods that support the waitable functionality.
		 * 
		 --*/

        //
        // Returns true if at least one permit is available.
        //

        internal override bool _AllowsAcquire {
            get { return (state != 0 && queue.IsEmpty); }
        }

        //
        // Tries to acquire one permit.
        //

        internal override bool _TryAcquire() {
            return TryAcquireInternal(1);            
        }

        //
        // Releases one permit.
        //

        internal override bool _Release() {
            if (!ReleaseInternal(1)) {
                return false;
            }
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue();
            }
            return true;
        }

        //
        // Executes the prologue of the Waitable.WaitAny method.
        //

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock ignored, ref int sc) {
            //
            // Try to acquire one permit.
            //

       	    if (TryAcquireInternal(1)) {

                //
                // We acquireed one permite. So try to lock the parker
                // and, if succeed, self unpark the current thread with
                // the specified *key*.
                //


                if (pk.TryLock()) {
                    pk.UnparkSelf(key);
                } else {

                    //
                    // The parker was already locked, so we should undo the
                    // previous acquire releasing one permit.
                    //

                    UndoAcquire(1);
                    if (IsReleasePending) {
                        ReleaseWaitersAndUnlockQueue();
                    }
                }

                //
                // Return null because no wait block was inserted on the
                // semaphore's wait queue.
                //

				return null;
			}

			//
			// There are no permits available, so create a wait block that
            // used the specified parker, insert it in semaphore's queue and
            // return the inserted wait block.
			//

            WaitBlock wb = new WaitBlock(pk, WaitType.WaitAny, 1, key);
            sc = EnqueueAcquire(wb, 1);
			return wb;
		}

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //        
        
        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock ignored,
                                                     ref int sc) {

            //
            // Check if the semaphore's state allows to acquire one permit.
            //
            
            if (_AllowsAcquire) {

				//
				// Lock the parker and, if this is the last cooperative release,
                // self unpark the current thread.
                //

                if (pk.TryLock()) {
                    pk.UnparkSelf(StParkStatus.StateChange);
                }
				return null;
			}

			//
			// There are no permits available on the semaphore; so, create
            // a wait block, insert it in semaphore's queue and return
            // the wait block.
			//

            WaitBlock wb = new WaitBlock(pk, WaitType.WaitAll, 1, StParkStatus.StateChange);
            sc = EnqueueAcquire(wb, 1);

            //
			// Return the inserted wait block.
			//
		
			return wb;
		}

        //
        // Undoes a waitable acquire operation.
        //

        internal override void _UndoAcquire() {
            UndoAcquire(1);
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue();
            }
        }

        //
        // Cancels the specified acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock ignored) {
            CancelAcquire(wb);
        }

        //
        // Return the exception that must be thrown when the signal
        // operation fails.
        //

        internal override Exception _SignalException {
            get {
                return new StSemaphoreFullException();
            }
        }
	}
}
