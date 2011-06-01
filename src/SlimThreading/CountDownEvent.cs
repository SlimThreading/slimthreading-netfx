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
	// This class implements a count down event.
	//

    public sealed class StCountDownEvent : StNotificationEventBase {

        private volatile int count;

        public StCountDownEvent(int count, int spinCount) : base(count == 0, spinCount) {
            this.count = count;
        }

        public StCountDownEvent(int count) : this (count, 0) { }

        //
        // Signals with the specified amount.
        //

        public bool Signal(int n) {
	        do {
                int c;
                int nc = (c = count) - n;
		        if (nc < 0) {
                    throw new InvalidOperationException();
		        }
                if (Interlocked.CompareExchange(ref count, nc, c) == c) {

                    //
			        // If the counter reaches zero, signal the associatted event.
                    //

			        if (c == n) {
                        waitEvent.Set();
                        return true;
			        }
			        return false;
		        }
	        } while (true);
        }

        //
        // Signals the count down event by one.
        //

        public override bool Signal() {
            return Signal(1);
        }

        //
        // Tries to add the specified amount to the count down event if its
        // state is not zero.
        //

        public bool TryAdd(int n) {
            if (n <= 0) {
                throw new ArgumentOutOfRangeException("n");
            }
            do {
                int c;
                if ((c = count) == 0) {
                    return false;
                }
                int nc = c + n;
                if (nc <= 0) {
                    throw new InvalidOperationException("Increment overflow");
                }
                if (Interlocked.CompareExchange(ref count, nc, c) == c) {
                    return true;
                }
            } while (true);
        }

        //
        // Tries to add one to the count down event state.
        //

        public bool TryAdd() {
            return TryAdd(1);
        }

        //
        // Returns the current value of te count down event.
        //

        public int Value {
            get { return count; }
        }

        //
		// Waits until the count down event reaches zero, activating
        // the specified cancellers.
		//

        public bool Wait(StCancelArgs cargs) {
            int ws = waitEvent.Wait(cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }

            StCancelArgs.ThrowIfException(ws);
            return false;
        }

        //
        // Waits unconditionally until the count down event reaches zero.
        //

        public bool Wait() {
            return Wait(StCancelArgs.None);
        }
    }
}
