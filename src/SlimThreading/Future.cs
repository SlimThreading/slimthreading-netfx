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
	// This class implements a future for the type T.
	//

    public sealed class StFuture<T> : StNotificationEventBase {
        
        //
        // The future state is non-zero if the futures value is available.
        //

        private volatile int wasSet;
        private T _value;

        //
        // Constructors.
        //

        public StFuture(int spinCount) : base(false, spinCount) { }

        public StFuture() : base(false, 0) { }

        //
        // Waits until the future's value is available, activating the
        // specified cancellers.
        //

        public bool Wait(out T result, StCancelArgs cargs) {
            
            //
            // If the event is signalled, the value is already available.
            //

            if (InternalIsSet) {
                result = _value;
                return true;
            }

            //
            // Waits on the event, activating the specified cancellers.
            //

            int ws = InternalWait(cargs);
            if (ws == StParkStatus.Success) {
                result = _value;
                return true;
            }

            //
            // The wait was cancelled; so report the failure appropriately.
            //

            result = default(T);
            StCancelArgs.ThrowIfException(ws);
            return false;
        }

	    //
	    // Get and set the future value.
	    //

        public T Value {
            get {
                if (InternalIsSet) {
                    return _value;
                }
                InternalWait(StCancelArgs.None);
                return _value;
            }

            set {

                //
                // Only the first setter defines the future's value.
                //

                if (wasSet == 0 && Interlocked.Exchange(ref wasSet, 1) == 0) {

                    //
                    // Set the future value and signal the associated event.
                    //

                    _value = value;
                    InternalSet();
                    return;
                }

                //
                // The future value was already set, so throw the appropriate exception.
                //
                
                throw new InvalidOperationException("The future value is alredy set");
            }
        }

        //
        // Returns the expection that must be thrown when the signal
        // operation fails.
        //

        internal override Exception _SignalException {
            get { return new InvalidOperationException("Futures can't be signalled"); }
        }
    }
}
