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
	//	This class implements a non-reentrant read/write lock.
	//

	public sealed class StReadWriteLock : StWaitable, IMonitorLock {

		//
		// The r/w lock *state* field is used as follows:
		// - bits 0-29: hold the current number of read locks.
		// - bit 30: is set when the write lock is locked.
		// - bit 31: unused.
		//

        internal const int WRITING = (1 << 30);
        private const int MAX_READERS = (WRITING - 1);

        //
        // Types of acquire requests.
        //

		private const int ENTER_READ = 1;
		private const int ENTER_WRITE = 2;
        private const int LOCKED_ENTER_WRITE = (WaitBlock.LOCKED_REQUEST | ENTER_WRITE);

		//
		// The r/w lock state and queue.
		//

		internal volatile int state;
		private LockedWaitQueue queue;

        //
        // The number of spin cycles that the first waiter thread
        // executes before blocks.
        //

        private int spinCount;

		//
		// Constructors.
		//

		public StReadWriteLock(int spinCount) {
			queue.Init();
            this.spinCount = Platform.IsMultiProcessor ? spinCount : 0;
		}

		public StReadWriteLock() : this(0) {}

		//
		// Tries to enter the write lock on behalf of the current thread.
		// 

		private bool TryEnterWriteInternal() {
			do {
                int s;
                if ((s = state) != 0 || !queue.IsEmpty) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, WRITING, 0) == 0) {
                    return true;
                }
			} while (true);
		}

		//
		// Tries to enter the write lock on behalf of the thread that is
        // at the front of the r/w lock's queue.
		// 

		private bool TryEnterWriteQueuedInternal() {
            return (state == 0 && Interlocked.CompareExchange(ref state, WRITING, 0) == 0);
		}

        //
        // Undoes an write lock enter.
        // 

        private void UndoEnterWrite() {
            Interlocked.Exchange(ref state, 0);
        }

		//
		// Tries to enter the read lock on behalf of the current thread.
		// 

		private bool TryEnterReadInternal() {
			do {
                int s;
                if ((s = state) >= MAX_READERS || !queue.IsEmpty) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, s + 1, s) == s) {
                    return true;
                }
			} while (true);
		}

		//
		// Tries to enter the read lock on behalf of the thread that is
        // at the front of the r/w lock's queue.
		// 

		private bool TryEnterReadQueuedInternal() {
			do {
                int s;
                if ((s = state) >= MAX_READERS) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref state, s + 1, s) == s) {
                    return true;
                }
			} while (true);
		}

        //
		// Undoes a read lock enter.
		// 

        private void UndoEnterRead() {
            Interlocked.Decrement(ref state);
        }

		//
		// Exits the read lock.
		//

		private bool ExitReadInternal() {
			return (Interlocked.Decrement(ref state) == 0);
		}

		//
		// Exits the write lock.
		//

		private void ExitWriteInternal() {
			Interlocked.Exchange(ref state, 0);
		}

        //
        // Returns true with the r/w lock's queue locked if any waiter
        // must be released; otherwise, returns false.
        //

        private bool IsReleasePending {
            get {
			    WaitBlock w = queue.First;
                return (w != null &&
                        (state == 0 ||
                         ((w.request & WaitBlock.MAX_REQUEST) == ENTER_READ && state < MAX_READERS) ||
                         (w.parker.IsLocked && w.request > 0)) &&
                        queue.TryLock());
            }
		}

		//
		// Releases the approprite waiters and unlocks the
        // r/w lock's queue.
		//

		private void ReleaseWaitersAndUnlockQueue() {
			do {
				WaitBlock qh = queue.head;
                WaitBlock w;
				while ((w = qh.next) != null) {
                    StParker pk = w.parker;
                    if (w.waitType == WaitType.WaitAny) {
                        int r = (w.request & WaitBlock.MAX_REQUEST);
                        if (r == ENTER_WRITE) {

                            //
                            // The next waiter is a writer, so try to enter the
                            // write lock.
                            //

                            if (!TryEnterWriteQueuedInternal()) {
                                break;
                            }

                            //
                            // Try to lock the associated parker and, if succeed, unpark
                            // its owner thread.
                            //

                            if (pk.TryLock() || w.request < 0) {
                                pk.Unpark(w.waitKey);

                                //
                                // Since that no more waiters can be released,
                                // advance the local queue's head and exit the
                                // inner loop.
                                //

                                qh.next = qh;
                                qh = w;
                                break;
                            } else {

                                //
                                // The acquire attempt was cancelled, so undo the
                                // previous acquire.
                                //

                                UndoEnterWrite();
                            }
                        } else {

                            //
                            // The next waiter is a reader, so try to acquire the
                            // read lock.
                            //

                            if (!TryEnterReadQueuedInternal()) {
                                break;
                            }

                            //
                            // Try to lock the associated parker and, if succeed, unpark
                            // its owner thread.
                            //

                            if (pk.TryLock() || w.request < 0) {
                                pk.Unpark(w.waitKey);
                            } else {

                                //
                                // The acquire attempt was cancelled, so undo the
                                // previous acquire.
                                //

                                UndoEnterRead();
                            }
                        }
                    } else {

                        //
                        // WaitOne-all.
                        // If the write lock is free, lock the parker and, if this is
                        // the last cooperative release, unpark its owner thread.
                        //

                        if (state == 0) {
                            if (pk.TryLock()) {
                                pk.Unpark(w.waitKey);
                            }
                        } else {

                            //
                            // The write lock is busy, so exit the inner loop.
                            //

                            break;
                        }
                    }

                    //
                    // Advance the local queue's head, marking the wait block
                    // referenced by the previous head as unlinked.
                    //

                    qh.next = qh;
                    qh = w;
				}

				//
				// It seems that no more waiters can be released; so, set the
                // new queue's head and unlock the queue.
                //

                queue.SetHeadAndUnlock(qh);

                //
                // After release the queue's lock, if it seems that more waiters
                // can released, repeate the release processing.
                //

                if (!IsReleasePending) {
                    return;
                }
			} while (true);
		}

		//
        // Cancels an enter lock attempt.
		//

	    private void CancelAcquire(WaitBlock wb) {
            WaitBlock wbn;

            if ((wbn = wb.next) != wb && wbn != null && queue.TryLock()) {
                queue.Unlink(wb);
                ReleaseWaitersAndUnlockQueue();
            }
		}

        //
        // Enqueues an enter read lock attempt.
        //

        private int EnqueueEnterRead(WaitBlock wb) {

            //
            // Enqueue the request in the r/w lock queue.
            //

            bool isFirst = queue.Enqueue(wb);

            //
            // If the was inserted at front of the r/w lock's queue, check
            // if we can acquire now; if so, execute the release processing.
            //

            if (isFirst && state < MAX_READERS && queue.TryLock()) {
                ReleaseWaitersAndUnlockQueue();
            }

            //
            // Retuen the number of spin cycles.
            //

            return (isFirst ? spinCount : 0);
        }

		//
		// Tries to enter the read lock activating, the specified
        // cancellers.
        //

		public bool TryEnterRead(StCancelArgs cargs) {

			//
			// Try to enter the read lock and, if succeed, return success.
			//

            if (TryEnterReadInternal()) {
                return true;
            }

			//
			// Return failure, if a null timeout was specified.
			//

            if (cargs.Timeout == 0) {
                return false;
            }

			//
			// Create a wait block, insert it in the r/w lock's queue.
 			//

            WaitBlock wb = new WaitBlock(WaitType.WaitAny, ENTER_READ, StParkStatus.Success);
            int sc = EnqueueEnterRead(wb);

            //
			// Park the current thread, activating the specified cancellers and
            // spinning if appropriate.
			//

			int ws = wb.parker.Park(sc, cargs);

            //
            // If we entered the read lock, return success.
            //

            if (ws == StParkStatus.Success) {
                return true;
            }

			//
			// The wait was cancelled; so, cancel the enter lock attempt
            // and report the failure appropriately.
			//

			CancelAcquire(wb);
            StCancelArgs.ThrowIfException(ws);
            return false;
		}

        //
        // Tries to enter the read lock immediately.
        //

		public bool TryEnterRead() {
            return TryEnterReadInternal();
        }

		//
		// Enters the read lock.
		//

		public void EnterRead() {
			TryEnterRead(StCancelArgs.None);
		}

        //
        // Enqueues an enter write lock attempt.
        //

        private int EnqueueEnterWrite(WaitBlock wb) {

            //
            // Enqueue the wait block in the r/w lock queue.
            //

            bool isFirst = queue.Enqueue(wb);

            //
            // If the wait block was inserted at front of the queue, check if
            // we can acquire now; if so, execute the release processing.
            //

            if (isFirst && state == 0 && queue.TryLock()) {
                ReleaseWaitersAndUnlockQueue();
            }
            return (isFirst ? spinCount : 0);
        }

        //
		// Waits until enter the write lock, activating the specified
        // cancellers.
		//

		public bool TryEnterWrite(StCancelArgs cargs) {

			//
			// Try to enter the write lock and, if succeed, return success.
			//

            if (TryEnterWriteInternal()) {
                return true;
            }
			
			//
			// Return failure, if a null timeout was specified.
			//

            if (cargs.Timeout == 0) {
                return false;
            }

			//
			// Create a wait block and insert it in the r/w lock's queue.
			//

            WaitBlock wb = new WaitBlock(WaitType.WaitAny, ENTER_WRITE, StParkStatus.Success);
            int sc = EnqueueEnterWrite(wb);

            //
			// Park the current thread, activating the specified cancellers
            // and spinning if appropriate.
			//

			int ws = wb.parker.Park(sc, cargs);

            //
            // If we entered the write lock, return success.
            //

            if (ws == StParkStatus.Success) {
                return true;
            }

			//
			// The request was cancelled; so, cancel the enter write lock
            // attempt and report the failure appropriately.
			//

			CancelAcquire(wb);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
		// Tries to enter the write lock immediately.
		//

		public bool TryEnterWrite() {
            return TryEnterWriteInternal();
        }

		//
		// Enters the write lock unconditionally.
		//

		public void EnterWrite() {
			TryEnterWrite(StCancelArgs.None);
		}

		//
		// Exits the read lock.
		//

		public void ExitRead() {

			//
			// Update the r/w lock's state and, if the lock becomes free,
            // try to release the appropriate waiters.
			//

            if (ExitReadInternal() && IsReleasePending) {
                ReleaseWaitersAndUnlockQueue();
            }
		}

		//
		// Exits the write lock.
		//

		public void ExitWrite() {

			//
			// Release the write lock and try to release the
            // appropriate waiters.
			//

			ExitWriteInternal();
            if (IsReleasePending) {
                ReleaseWaitersAndUnlockQueue();
            }
		}

		/*++
		 * 
		 * IMonitorLock interface implementation.
		 * 
		 --*/

		//
		// Returns true if the write lock is busy.
		//

        bool IMonitorLock.IsOwned {
            get { return state == WRITING; }
        }

		//
		// Exits the write lock.
		//

        int IMonitorLock.ExitCompletely() {
			ExitWrite();
			return 0;
		}

		//
		// Reenters the write lock.
		//

        void IMonitorLock.Reenter(int waitStatus, int ignored) {
            if (waitStatus != StParkStatus.Success) {
                EnterWrite();
            }
		}

		//
		// Enqueue a locked enter write request in the r/w lock's queue.
		//

        void IMonitorLock.EnqueueWaiter(WaitBlock waitBlock) {
			waitBlock.request = LOCKED_ENTER_WRITE;
			queue.Enqueue(waitBlock);
		}

		/*++
		 * 
		 * Virtual methods that support the Waitable functionality.
		 * 
		 --*/

        //
        // Return true if the write lock is free.
        //

        internal override bool _AllowsAcquire {
            get { return state == 0 && queue.IsEmpty; }
        }

		//
		// Tries to enter the write lock.
		//
	
		internal override bool _TryAcquire() {
			return TryEnterWriteInternal();
		}

        //
        // Releases the write lock.
        //

        internal override bool _Release() {
            if (state == WRITING) {
                ExitWrite();
                return true;
            }
            return false;
        }

        //
        // Returns the exception that must be thrown when the signal
        // operation fails.
        //

        internal override Exception _SignalException {
            get { return new StSynchronizationLockException(); }
        }

		//
		// Executes the prologue of the Waitable.WaitAny method.
		//

		internal override WaitBlock _WaitAnyPrologue(StParker pk, int key,
                                                     ref WaitBlock ignored, ref int sc) {
			if (TryEnterWriteInternal()) {
				return null;
			}

			var wb = new WaitBlock(pk, WaitType.WaitAny, ENTER_WRITE, key);
            sc = EnqueueEnterWrite(wb);
			return wb;
		}

        //
        // Executes the prologue of the Waitable.WaitAll method.
        //

        internal override WaitBlock _WaitAllPrologue(StParker pk,
                                                     ref WaitBlock ignored, ref int sc) {
            if (_AllowsAcquire) {
                return null;
            }

            var wb = new WaitBlock(pk, WaitType.WaitAll, ENTER_WRITE, StParkStatus.StateChange);
            sc = EnqueueEnterWrite(wb);
            return wb;
        }

        //
        // Releases the write lock to undo a previous acquire.
        //

        internal override void _UndoAcquire() {
            ExitWrite();
        }

        //
        // Cancels an acquire attempt.
        //

        internal override void _CancelAcquire(WaitBlock wb, WaitBlock ignored) {
            CancelAcquire(wb);
        }    
    }
}
