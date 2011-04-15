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
	// This class implements a non-fair reentrant lock.
	//

	public sealed class StReentrantLock : IMonitorLock {

		//
		// Constant used for owner when the lock is free.
		//

		private const int UNOWNED = 0;

        //
        // The associated non-reentrant lock.
        //

        private readonly StLock nrlock;

        //
        // The lock owner and the recursive acquisition count.
        //

		private int owner;
		private int count;

		//
		// Constructors.
		//

		public StReentrantLock(int spinCount) {
            nrlock = new StLock(spinCount);
		}

        public StReentrantLock() {
            nrlock = new StLock();
        }

        //
        // Tries to enter the lock immediately.
        //

        public bool TryEnter() {

            //
            // First check if the lock is free and, if so, try to acquire it.
            //

            int tid = Thread.CurrentThread.ManagedThreadId;
            if (nrlock.state == StLock.FREE &&
                Interlocked.CompareExchange(ref nrlock.state, StLock.BUSY,
                                            StLock.FREE) == StLock.FREE) {

                //
                // Set the owner thread, since that a free lock has a zero
                // recursive acquisition count.
                //

                owner = tid;
                return true;
            }
            if (owner == tid) {

                //
                // Recursive enter, so increment the recursive acquisition
                // counter.
                //

                count++;
                return true;
            }
            return false;
        }

        //
        // Enters the lock unconditionally.
        //

        public void Enter() {

            //
            // First check if the lock is free and, if so, try to acquire it.
            //

            int tid = Thread.CurrentThread.ManagedThreadId;
            if (nrlock.state == StLock.FREE &&
                Interlocked.CompareExchange(ref nrlock.state, StLock.BUSY,
                                            StLock.FREE) == StLock.FREE) {
                owner = tid;
                return;
            } 
            if (owner == tid) {

		        //
		        // Recursive enter, so increment the recursive acquisition count.
		        //
		
		        count++;
	        } else {

		        //
		        // Acquire the associated non-reentrant lock.
		        //
		
                nrlock.Enter();
        
                //
		        // Set the owner thread, since that a free lock has a zero
                // recursive acquisition count.
		        //
		
		        owner = tid;
	        }
        }

        //
        // Tries to enter the lock, activating the specifed cancellers.
        //

        public bool TryEnter(StCancelArgs cargs) {

            //
            // First check if the lock is free and, if so, try to acquire it.
            //

            int tid = Thread.CurrentThread.ManagedThreadId;
            if (nrlock.state == StLock.FREE &&
                Interlocked.CompareExchange(ref nrlock.state, StLock.BUSY,
                                            StLock.FREE) == StLock.FREE) {

                //
                // Set the owner thread, since that a free lock has a zero
                // recursive acquisition count.
                //

                owner = tid;
                return true;
            }
            if (owner == tid) {

                //
                // Recursive enter, so increment the recursive acquisition
                // counter.
                //

                count++;
                return true;
            }

            if (cargs.Timeout != 0 && nrlock.TryEnter(cargs)) {
                owner = tid;
                return true;
            }

            //
            // The acquire attempt was cancelled, so return failure.
            //

            return false;
        }
        
        //
        // Exits the lock.
        //

        public void Exit() {
            if (owner != Thread.CurrentThread.ManagedThreadId) {
                throw new StSynchronizationLockException();
            }

            //
            // If this is the last lock release, free it; otherwise,
            // decrement the recursive acquisition counter.
            //

            if (count == 0) {

		        //
	        	// Clear the owner thread and release the associated
                // non-reentrant lock.
	        	//
		
		        owner = UNOWNED;
                nrlock.Exit();
	        } else {
		        count--;	
	        }
        }

		/*++
         * 
         * IMonitorLock interface implementation.
         * 
         --*/

		//
		// Returns true if the lock is held by the current thread.
		//
		
		bool IMonitorLock.IsOwned {
			get { return owner == Thread.CurrentThread.ManagedThreadId; }
		}

		//
		// Exits the lock completely and returns the current lock state.
		//

        int IMonitorLock.ExitCompletely() {
		    int pc = count;
			count = 0;
			owner = UNOWNED;
			nrlock.Exit();
			return pc;
		}

		//
		// Reenters the lock and restores the previous state.
		//

        void IMonitorLock.Reenter(int ignored, int pvCount) {
			nrlock.Enter();
			owner = Thread.CurrentThread.ManagedThreadId;
            count = pvCount;
		}

		//
		// Enqueues the specified wait block non-reentrant lock's queue,
		// when the current thread owns the lock.
		//

        void IMonitorLock.EnqueueWaiter(WaitBlock wb) {
            ((IMonitorLock)nrlock).EnqueueWaiter(wb);
		}
	}
}
