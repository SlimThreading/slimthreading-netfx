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

namespace SlimThreading {

    //
    // The interface that is implemented by the locks that can be
    // used as the monitor lock by condition variables.
    //

    internal interface IMonitorLock {
        bool IsOwned { get; }
        int ExitCompletely();
        void Reenter(int ws, int prevState);
        void EnqueueWaiter(WaitBlock wb);
    }

	//
	// This class implements a condition variable that can be used
    // with any lock that implement the IMonitorLock interface.
	//

	public sealed class StConditionVariable {
		private readonly IMonitorLock mlock;
        private WaitBlockQueue queue;

		private StConditionVariable(IMonitorLock mlock) {
			this.mlock = mlock;
			queue = new WaitBlockQueue();
		}

        public StConditionVariable(StReentrantLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StFairLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReentrantFairLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReadWriteLock mlock) : this((IMonitorLock)mlock) { }

        public StConditionVariable(StReentrantReadWriteLock mlock) : this((IMonitorLock)mlock) { }

		//
		// Notifies a thread waiting on the condition variable.
		//

		public void Pulse() {
            EnsureIsOwned();

            WaitBlock w;        
            while ((w = queue.Dequeue()) != null && !TryEnqueue(w)) { }
		}

		//
		// Notifies all threads waiting on the condition variable.
		//

		public void PulseAll() {
            EnsureIsOwned();

		    WaitBlock w;
            while ((w = queue.Dequeue()) != null) {
                TryEnqueue(w);
            }
        }

		//
		// Waits until notification, activating the specified
        // cancellers.
		//

		public bool Wait(StCancelArgs cargs) {
            EnsureIsOwned();

            WaitBlock wb;
            queue.Enqueue(wb = new WaitBlock(WaitType.WaitAny));

			int lockState = mlock.ExitCompletely();
            int ws = wb.parker.Park(cargs);
            mlock.Reenter(ws, lockState);

            if (ws == StParkStatus.Success) {
                return true;
            }

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

        private void EnsureIsOwned() {
            if (!mlock.IsOwned) {
                throw new StSynchronizationLockException();
            }
        }

        private bool TryEnqueue(WaitBlock w) {
            if (w.parker.TryLock()) {

                //
                // Move the locked wait block to the lock's queue.
                //

                mlock.EnqueueWaiter(w);
                return true;
            }
            
            //
            // Mark the WaitBlock as unlinked so that WaitBlockQueue::Remove 
            // returns immediately.
            //

            w.next = w;
            return false;
        }
	}
}
