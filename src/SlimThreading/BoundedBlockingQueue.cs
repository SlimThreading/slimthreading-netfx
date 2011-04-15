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
    // This class implements a generic bounded blocking.
    //
    
    public class StBoundedBlockingQueue<T> : StUnboundedBlockingQueue<T> {

		//
		// The embedded semaphore to control the free slots.
		//

		private volatile int free;
		private readonly NonBlockingFifoWaitQueue waitQueue;

		//
		// Constructors.
		//

		public StBoundedBlockingQueue(int capacity, bool lifoQueue) : base(lifoQueue) {
			if (capacity < 1) {
				throw new ArgumentOutOfRangeException("capacity");
            }
			free = capacity;
			waitQueue = new NonBlockingFifoWaitQueue();
		}

        public StBoundedBlockingQueue(int capacity) : this(capacity, false) { }

		//
		// Tries to reserve a free slot.
		//

		private bool TryReserveSlot() {
			do {
                int f;
                if ((f = free) == 0) {
                    return false;
                }
                if (Interlocked.CompareExchange(ref free, f - 1, f) == f) {
                    return true;
                }
			} while (true);
		}

		//
		// Frees a queue's slot.
		//

		private void FreeSlot() {

			//
			// If there is at least a thread blocked on the semaphore,
            // use the freed slot to store its data item and release it.
			//

			WaitNode w;
			if ((w = waitQueue.TryDequeueAndLock()) != null) {
				AddWorker(w.channel);
				w.parker.Unpark(w.waitKey);
				return;
			}

            //
            // The wait queue is empty, so increment the number of
            // free slots
            //

			Interlocked.Increment(ref free);

            //
            // If the wait queue is still empty, return.
            //

            if (waitQueue.IsEmpty) {
                return;
            }

			//
			// Try to release one of the waiter thraeds.
			//

            do {

                //
                // Try to acquire a free slot on behalf of a waiter thread.
                //

                if (!TryReserveSlot()) {
                    return;
                }

                //
                // We got a free queue slot, so try to release a thread
                // waiting to add.
                //

                if ((w = waitQueue.TryDequeueAndLock()) != null) {
                    AddWorker(w.channel);
                    w.parker.Unpark(w.waitKey);
                    return;
                }

                //
                // Release the free slot, and retry the release process if
                // wait queue is not empty.
                //

                Interlocked.Increment(ref free);
            } while (!waitQueue.IsEmpty);
		}

        //
        // Tries to add immediately data item immediately to the queue.
        //

        public override bool TryAdd(T di) {
		    if (TryReserveSlot()) {
                AddWorker(di);
                return true;
            }
            return false;
        }

        //
        // Executes the prologue of the TryAdd operation.
        //

        internal override WaitNode TryAddPrologue(StParker pk, int key, T di, ref WaitNode hint) {
            if (TryReserveSlot()) {
                if (pk.TryLock()) {
                    AddWorker(di);
                    pk.UnparkSelf(key);
                } else {
                    FreeSlot();
                }
                return null;
            }

            //
            // ...
            //

            WaitNode wn;
            hint = waitQueue.Enqueue(wn = new WaitNode(pk, key, di));

            //
            // As a slot could have been free after the check done
            // above, but before we insert the wait block in the wait queue,
            // we must retry to reserve a free slot.
            //

            if (TryReserveSlot()) {
                if (pk.TryLock()) {
                    AddWorker(di);
                    pk.UnparkSelf(key);
                } else {
                    waitQueue.Unlink(wn, hint);
                    FreeSlot();
                }
                return null;
            }
            return wn;
        }

        //
        // Cancels the specifie add attempt.
        //

        internal override void CancelAddAttempt(StBlockingQueue<T>.WaitNode wn,
                                                StBlockingQueue<T>.WaitNode hint) {
            waitQueue.Unlink(wn, hint);
        }

        //
        // Tries to take a data item immediately from the queue.
        //

        public override bool TryTake(out T di) {
            if (base.TryTake(out di)) {
                FreeSlot();
                return true;
            }
            return false;
        }

        //
        // Executes the prologue of the TryTake operation.
        //

        internal override WaitNode TryTakePrologue(StParker pk, int key, out T di, ref WaitNode hint) {
            WaitNode wn = base.TryTakePrologue(pk, key, out di, ref hint);
            if (wn == null) {
                FreeSlot();
            }
            return wn;
        }

		//
		// Frees the slot emptyied by a take operation.
		//

		internal override void TakeEpilogue() {
			FreeSlot();
		}
	}
}
