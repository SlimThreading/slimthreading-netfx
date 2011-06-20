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
	// This class implements a semaphore.
	//

	public sealed class StSemaphore : StWaitable {
        private volatile int state;
        private LockedWaitQueue queue;
        private readonly int maximumCount;
        private readonly int spinCount;

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

        public StSemaphore(int count, int maximumCount) : this(count, maximumCount, 0) { }

		public StSemaphore(int count) : this(count, Int32.MaxValue, 0) { }

        internal override bool _AllowsAcquire {
            get { return state != 0 && queue.IsEmpty; }
        }

        //
        // If at least a waiter can be released, or the first is waiter is locked,
        // returns true with the semaphore's queue locked; otherwise returns false.
        //

        private bool IsReleasePending {
            get {
                WaitBlock w = queue.First;
                return w != null && (state >= w.request || w.parker.IsLocked) && queue.TryLock();
            }
        }

        //
		// Waits until acquire the specified number of permits, activating
        // the specified cancellers.
        //

		public bool WaitOne(int acquireCount, StCancelArgs cargs) {
            if (acquireCount <= 0 || acquireCount > maximumCount) {
                throw new ArgumentException("acquireCount");
            }

			if (TryAcquireInternal(acquireCount)) {
                return true;
            }

			if (cargs.Timeout == 0) {
                return false;
            }

            var wb = new WaitBlock(WaitType.WaitAny, acquireCount);
            int sc = EnqueueAcquire(wb, acquireCount);

            int ws = wb.parker.Park(sc, cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }

			CancelAcquire(wb);
            StCancelArgs.ThrowIfException(ws);
            return false;
		}

		//
		// Waits unconditionally until acquire the specified number
        // of permits.
        //

		public void WaitOne(int acount) {
		    WaitOne(acount, StCancelArgs.None);
        }
        
        //
		// Releases the specified number permits.
		//

		public int Release(int rcount) {
            if (rcount < 1) {
                throw new ArgumentOutOfRangeException("rcount", "\"rcount\" must be positive");
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
                ReleaseWaitersAndUnlockQueue(null);
            }
            return prevCount;
		}
		
		internal override bool _TryAcquire() {
            return TryAcquireInternal(1);            
        }

        internal override bool _Release() {
            if (!ReleaseInternal(1)) {
                return false;
            }
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue(null);
            }
            return true;
        }

        internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock ignored, ref int sc) {
            if (TryAcquireInternal(1)) {
                return null;
			}

			var  wb = new WaitBlock(pk, WaitType.WaitAny, 1, key);
            sc = EnqueueAcquire(wb, 1);
			return wb;
		}

        internal override WaitBlock _WaitAllPrologue(StParker pk, ref WaitBlock ignored,
                                                     ref int sc) {
            if (_AllowsAcquire) {
                return null;
			}

			var wb = new WaitBlock(pk, WaitType.WaitAll, 1, StParkStatus.StateChange);
            sc = EnqueueAcquire(wb, 1);
            return wb;
		}

        internal override void _UndoAcquire() {
            UndoAcquire(1);
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue(null);
            }
        }

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock ignored) {
            CancelAcquire(wb);
        }

        internal override Exception _SignalException {
            get { return new StSemaphoreFullException(); }
        }

        //
		// Tries to acquire the specified number of permits on behalf
        // of the current thread if the queue is empty.
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
        // Only one thread act as a releaser at any given time.
        //

		private void ReleaseWaitersAndUnlockQueue(WaitBlock self) {
			do {
				WaitBlock qh = queue.head;
                WaitBlock w;

                while (state > 0 && (w = qh.next) != null) {
                    StParker pk = w.parker;

                    if (w.waitType == WaitType.WaitAny) {
                        if (!TryAcquireInternalQueued(w.request)) {
                            break;
                        }

                        if (pk.TryLock()) {
                            if (w == self) {
                                pk.UnparkSelf(w.waitKey);
                            } else {
                                pk.Unpark(w.waitKey);
                            }
                        } else {
                            UndoAcquire(w.request);
                        }
                    } else if (pk.TryLock()) {
                        if (w == self) {
                            pk.UnparkSelf(w.waitKey);
                        } else {
                            pk.Unpark(w.waitKey);
                        }
                    }

                    //
                    // Remove the wait block from the semaphore's queue,
                    // marking the previous head as unlinked, and advance 
                    // the local queues's head.
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
                // If after the semaphore's queue is unlocked, it seems that
                // more waiters can be released, repeat the release processing.
                //

                if (!IsReleasePending) {
                    return;
                }
			} while (true);
		}

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

        private int EnqueueAcquire(WaitBlock wb, int acquireCount) {
	        bool isFirst = queue.Enqueue(wb);

	        //
	        // If the wait block was inserted at the front of the queue and
	        // the current thread can now acquire the requested permits, try 
	        // to lock the queue and execute the release processing.
	        //

	        if (isFirst && state >= acquireCount && queue.TryLock()) {
	            ReleaseWaitersAndUnlockQueue(wb);
	        }

	        return isFirst ? spinCount : 0;
	    }

	    private void UndoAcquire(int undoCount) {
	        do {
	            int s = state;
	            if (Interlocked.CompareExchange(ref state, s + undoCount, s) == s) {
	                return;
	            }
	        } while (true);
	    }

	    private void CancelAcquire(WaitBlock wb) {

            //
            // If the wait block is still linked and it isn't the last wait block
            // in the queue and the queue's lock is free, unlink the wait block.
            //

            WaitBlock wbn;
            if ((wbn = wb.next) != wb && wbn != null && queue.TryLock()) {
                queue.Unlink(wb);
                ReleaseWaitersAndUnlockQueue(null);
            }
        }
	}
}
