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
using System.Runtime.Serialization;

namespace SlimThreading {

    //
    // Exceptions defined by the SlimThreading library.
    //
    //
    // The exception thrown when a wait is alerted.
    //

    public class StThreadAlertedException : SystemException {

        //
        // Constructors.
        //

        public StThreadAlertedException() : base("WaitOne alerted") {}

        public StThreadAlertedException(string msg) : base(msg) {}

        public StThreadAlertedException(string msg, Exception innerEx)
                            : base(msg, innerEx) {}
    }

    //
    // The exception thrown when a release operation tries to exceed
    // the previously specified semaphore.
    //

    public class StSemaphoreFullException : SystemException {

        //
        // Constructors.
        //

        public StSemaphoreFullException() : base("Semaphore full exception") { }

        public StSemaphoreFullException(string msg) : base(msg) { }

        public StSemaphoreFullException(string msg, Exception innerEx) : base(msg, innerEx) { }
    }

    //
    // The exception thrown when a thread tries to release a lock that
    // it isn't the owner.
    //

    public class StSynchronizationLockException : SystemException {

        //
        // Constructors.
        //

        public StSynchronizationLockException() :
                        base("Lock isn't owned by the current thread") {}

        public StSynchronizationLockException(string msg) : base(msg) {}

        public StSynchronizationLockException(string msg, Exception innerEx) :
                        base(msg, innerEx) {}
    }

    //
    // The exception that is thrown when an exception is thrown by a
    // barrier's post phase action.
    //

    public class StBarrierPostPhaseException : Exception {

        //
        // Constructors.
        //

        public StBarrierPostPhaseException(string msg, Exception innerEx)
                        : base((msg == null) ? "Barrier post phase exception" : msg, innerEx) {}

        public StBarrierPostPhaseException(string msg) : this(msg, null) {}
        public StBarrierPostPhaseException() : this(null, null) {}

        public StBarrierPostPhaseException(Exception innerEx) : this(null, innerEx) {}

    }
}
