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

namespace SlimThreading {

    //
    // The interface that is implemented by the locks that can be
    // used as monitor's lock with condition variables.
    //

    internal interface IMonitorLock {
        bool IsOwned { get; }
        int ExitCompletely();
        void Reenter(int ws, int prevState);
        void EnqueueWaiter(WaitBlock wb);
    }

	//
	// This class implements a condition variable that can be used
    // with the locks that implement the IMonitorLock interface.
	//

	public sealed class StConditionVariable {
		
		//
		// The monitor lock.
		//
		
		private IMonitorLock mlock;

		//
		// The condition's queue.
		//

		private WaitBlockQueue queue;

		//
		// Constructors.
		//

		private StConditionVariable(IMonitorLock mlock) {
			this.mlock = mlock;
			queue = new WaitBlockQueue();
		}

        private StConditionVariable(StLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReentrantLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StFairLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReentrantFairLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReadWriteLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReentrantReadWriteLock mlock) : this((IMonitorLock)mlock) { }

		//
		// Notifies a thread waiting on the condition variable.
		//

		public void Pulse() {

            //
            // Ensure that the current thread is the owner of the monitor's lock.
            //

            if (!mlock.IsOwned) {
                throw new StSynchronizationLockException();
            }

            //
            // Try to release a thread blocked on the condition's queue.
            //

            WaitBlock w;        
            while ((w = queue.head) != null) {
                queue.Dequeue();
                if (w.parker.TryLock()) {

                    //
                    // Move the locked wait block to the monitor lock's queue
                    // and return.
                    //

                    mlock.EnqueueWaiter(w);
                    return;
                }
            }
		}

		//
		// Notifies all threads waiting on the condition variable.
		//

		public void PulseAll() {

            //
            // Ensure that the current thread is the owner of the monitor's lock.
            //

            if (!mlock.IsOwned) {
                throw new StSynchronizationLockException();
            }

			WaitBlock w;
            if ((w = queue.head) != null) {
                WaitBlock n;
                do {
                    n = w.next;
                    if (w.parker.TryLock()) {

                        //
                        // Move the locked wait block to the lock's queue.
                        //

                        mlock.EnqueueWaiter(w);
                    } else {
                        w.next = w;
                    }
                } while ((w = n) != null);
                queue.Clear();
            }
        }

		//
		// Waits until notification, activating the specified
        // cancellers.
		//

		public bool Wait(StCancelArgs cargs) {

            //
            // Ensure that the current thread is the owner of the monitor's lock.
            //

            if (!mlock.IsOwned) {
                throw new StSynchronizationLockException();
            }

            //
            // If a null timeout was specified, return failure.
            //

            if (cargs.Timeout == 0) {
                return false;
            }

            //
			// Create a wait block and insert it in the condition's queue.
			//

            WaitBlock wb;
            queue.Enqueue(wb = new WaitBlock(WaitType.WaitAny));

			//
			// Release the associated lock, saving its state.
			//

			int lockState = mlock.ExitCompletely();

			//
			// Park the current thread, activating the specified cancellers.
			//

			int ws = wb.parker.Park(cargs);

			//
			// Re-enter the monitor's lock.
			//

			mlock.Reenter(ws, lockState);

            //
            // If we were notified, return success.
            //

            if (ws == StParkStatus.Success) {
                return true;
            }

			//
			// The wait was cancelled; so, remove the wait block from
            // the wait queue and report the failure appropriately.
			//

            queue.Remove(wb);
            StCancelArgs.ThrowIfException(ws);
            return false;
		}

        //
		// Waits unconditionally until notification.
		//

        public void Wait() {
            Wait(StCancelArgs.None);
        }
	}
}
