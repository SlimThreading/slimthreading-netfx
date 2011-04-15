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

#pragma warning disable 0649

namespace SlimThreading {

    //
    // This class implements the lazy initialization functionality.
    //
        
    public static class StLazyInitializer {

        //
        // The lazy help class that defines a factory method for the type T.
        //

        private static class LazyHelper<T> {
            internal static Func<T> factory = new Func<T>(ActivatorFactory);

            //
            // Constructs an instance of T using the activator and the
            // default constructor fot T.
            //

            private static T ActivatorFactory() {
                T v;
                try {
                    v = (T)Activator.CreateInstance(typeof(T));
                } catch (MissingMethodException) {
                    throw new InvalidOperationException("No parameterless ctor for the type");
                }
                return v;
            }
        }

        //
        // The object reference used to generate acquire memory barriers.
        //

        private static volatile object barrier;

        //
        // Initializates a reference type when it seems to be non-initialized.
        //

        private static T EnsureInitializedCore<T>(ref T target, Func<T> factory) where T : class {
            T v = factory();
            if (v == null) {
                throw new InvalidOperationException("Factory method returned a null reference");
            }
            Interlocked.CompareExchange<T>(ref target, v, null);
            return target;
        }

        //
        // Initializes a reference type using the specified factory method.
        //

        public static T EnsureInitialized<T>(ref T target, Func<T> factory) where T : class {
            if (target != null) {
                object acquire = barrier;
                return target;
            }
            return EnsureInitializedCore<T>(ref target, factory);
        }

        //
        // Initializes a reference type using the default constructor.
        //

        public static T EnsureInitialized<T>(ref T target) where T : class {
            if (target != null) {
                object acquire = barrier;
                return target;
            }
            return EnsureInitializedCore<T>(ref target, LazyHelper<T>.factory);
        }

        //
        // Initialized a reference or value type using a factory method
        // and specifiyng the amount of spin cycles that a thread executes
        // before block.
        //

        public static T EnsureInitialized<T>(ref T target, ref StInitOnceLock initLock,
                                             Func<T> factory, int spinCount) {
            if (initLock.TryInit(spinCount)) {
                try {
                    target = factory();
                    initLock.InitCompleted();
                } catch {
                    initLock.InitFailed();
                    throw;
                }
            }
            return target;
        }

        //
        // Initialized a reference or value type using a factory method,
        // blocking immediately when the init lock is busy.
        //

        public static T EnsureInitialized<T>(ref T target, ref StInitOnceLock initLock,
                                             Func<T> factory) {
            return EnsureInitialized<T>(ref target, ref initLock, factory, 0);
        }


        //
        // Initialized a reference or value type using the default constructor
        // and specifiyng the amount of spin cycles that a thread executes
        // before block.
        //

        public static T EnsureInitialized<T>(ref T target, ref StInitOnceLock initLock,
                                             int spinCount) {
            return EnsureInitialized<T>(ref target, ref initLock, LazyHelper<T>.factory,
                                        spinCount);
        }

        //
        // Initialized a reference or value type using the default constructor,
        // blocking immediately when the init lock is busy.
        //

        public static T EnsureInitialized<T>(ref T target, ref StInitOnceLock initLock) {
            return EnsureInitialized<T>(ref target, ref initLock, LazyHelper<T>.factory, 0);
        }
    }
}
