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
using System.Runtime.InteropServices;
using System.Threading;

#pragma warning disable 0420

namespace SlimThreading {

    public class StWaitableWorkStealingQueue<T> : StBlockingQueue<T> where T : class {
        [ThreadStatic]
        private static StWaitableWorkStealingQueue<T> current;
        private readonly NonBlockingLifoWaitQueue waitQueue = new NonBlockingLifoWaitQueue();
        private readonly StWorkStealingQueue<T> dataQueue = new StWorkStealingQueue<T>();

        public static StWaitableWorkStealingQueue<T> Current {
            get { return current ?? (current = new StWaitableWorkStealingQueue<T>()); }
            set { current = value; }
        }
        
        public override bool TryAdd(T di) {
            WaitNode w;
            if ((w = waitQueue.TryDequeueAndLock()) != null) {
                w.channel = di;
                w.parker.Unpark(w.waitKey);
                return true;
            }
            
            dataQueue.LocalPush(di);

            if (waitQueue.IsEmpty) { 
                return true;
            }

            //
            // We missed a waiter, so try to satisfy it now by getting
            // the item we just tried to insert.
            //

            if (!dataQueue.LocalPop(out di)) {
                return true;
            }

            //
            // We have an item. Try to lock the node and unpark the waiter.
            //

            if ((w = waitQueue.TryDequeueAndLock()) != null) {
                w.channel = di;
                w.parker.Unpark(w.waitKey);
            }

            //
            // We have the item, but failed to lock the node. Perform a tail call
            // to retry the process.
            //

            return TryAdd(di);
        }

        internal override WaitNode TryAddPrologue(StParker pk, int key, T di, ref WaitNode hint) {
            if (pk.TryLock()) {
                pk.UnparkSelf(key);
            }

            TryAdd(di);
            return null;
        }

        public override bool TryTake(out T di) {
            return TryTake(out di, Current);
        }

        internal override WaitNode TryTakePrologue(StParker pk, int key, out T di, ref WaitNode hint) {
            var localQueue = Current;

            if (TryTake(out di, localQueue)) {
                if (pk.TryLock()) {
                    pk.UnparkSelf(key);
                } else {
                    localQueue.TryAdd(di);
                }
                return null;
            }

            var wn = new WaitNode(pk, key);
            waitQueue.Enqueue(wn);

            if (!pk.IsLocked && TryTake(out di, localQueue)) {
                if (pk.TryLock()) {
                    waitQueue.Unlink(wn, hint);
                    pk.UnparkSelf(key);
                    return null;
                }

                localQueue.TryAdd(di);
            }

            return wn;
        }

        private bool TryTake(out T di, StWaitableWorkStealingQueue<T> localQueue) {
            if (localQueue == this) {
                return dataQueue.LocalPop(out di);
            }

            bool missedSteal = false;

            do {
                if (dataQueue.TrySteal(out di, ref missedSteal)) {
                    return true;
                }
            } while (missedSteal);
            return false;
        }

        internal override void CancelTakeAttempt(WaitNode wn, WaitNode hint) {
            waitQueue.Unlink(wn, hint);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = CACHE_LINE_SIZE * 2)]
    internal struct Fields {
        private const int CACHE_LINE_SIZE = 64;

        [FieldOffset((CACHE_LINE_SIZE * 0) + CACHE_LINE_SIZE / 2)]
        public volatile int Head;

        [FieldOffset((CACHE_LINE_SIZE * 1) + CACHE_LINE_SIZE / 2)]
        public volatile int Tail;
    }

    public class StWorkStealingQueue<T> where T : class {
        private const int INITIAL_SIZE = 1 << 5;
        
        internal T[] items = new T[INITIAL_SIZE];
        private int mask = INITIAL_SIZE - 1;

        private Fields fields;

        private volatile int searchInProgress;

        public int Count {
            get {
                int count = 0;
                for (int i = fields.Head; i < fields.Tail; ++i) {
                    if (items[i & mask] != null) {
                        ++count;
                    }
                }
                return count;
            }
        }

        public bool IsEmpty {
            get { return fields.Head >= fields.Tail; }
        }

        public bool LocalRemove(T item) {
            int tail = fields.Tail;
            for (int i = tail - 1; i >= fields.Head; i--) {
                int index = i & mask;

                if (!item.Equals(items[index])) {
                    continue;
                }

                //
                // The order of the following instructions is strict. If a stealer saw the
                // item as null but not *searchInProgress* as its *head*, then if the CAS
                // that the stealer performs succeed he would loop because the item would
                // be null and here we would simply fail. An item would be missed.
                //
                // Although the .NET memory model at the time of writing does not reorder
                // writes, in order to work well with the ECMA model we employ a full fence.
                //

                Interlocked.Exchange(ref searchInProgress, i);
                items[index] = null;

                //
                // Synchronize with a stealer that sees *head* == i.
                //
								
                int actualHead = Interlocked.CompareExchange(ref fields.Head, i + 1, i);

                searchInProgress = -1;

                if (i == tail) {
                    fields.Tail = tail - 1;
                }

                return actualHead <= i;
            }
            return false;
        }

        public void LocalPush(T item) {
            int oldHead = fields.Head;
            int tail = fields.Tail;
            int size = tail - oldHead;
            int mask = this.mask;

            //
            // If size is equal to mask, then *tail* points to the last free position. 
            // Otherwise, *tail* is outside the bounds of the array and we must 
            // grow it. We ignore any steals that might happen after the test 
            // and grow the array despite the possibility of having free space.
            //

            if (size == mask) {
                 
                //
                // We start copying the items to the new array from the *oldHead* index.
                // Any elements that may have already been stolen will remain in the array.
                //

                T[] oldItems = items;

                int newSize = items.Length << 1;
                var newItems = new T[newSize];
                int newMask = this.mask = newSize - 1;

                for (int i = oldHead; i < tail; i++) {
                    newItems[i & newMask] = oldItems[i & mask];
                }

                mask = newMask;

                //
                // After the new tail is published, at most one stealer will successfully
                // steal from the old array (because stealers are effectively serialized 
                // by their CAS operation). This also means that there will be no conflict
                // between a steal from the old array and a remove from the new array, 
                // where a stealer would steal an item from the old array that had been
                // removed from the new. This is because the only conflict would have to
                // be when trying to remove the *head* position, but this is already solved
                // in the LocalRemove operation.
                //

                items = newItems;
            }

            items[tail & mask] = item;

            tail += 1;
            if (tail != int.MaxValue) {
                fields.Tail = tail;
                return;
            }
            
            fields.Tail = tail & mask;
            int head = fields.Head;
            do {
                if (tail - head >= -1) {
                    return;
                }

                int newHead;
                if ((newHead = Interlocked.CompareExchange(ref fields.Head, head & mask, head)) == head) {
                    return;
                }
                head = newHead;
            } while (true);
        }

        //
        // If the queue is empty, because size <= 0, it would seem possible to
        // reset the indexes to zero. However, doing so would make the following
        // execution possible, which allows for the ABA problem:
        //      1) A stealer thread sees *head* == 0 and *tail* == 1;
        //      2) Another stealer advances *head*;
        //      3) The owner thread sees *head* == 1 and *tail* == 1 and resets the indexes to zero;
        //      4) The stealer successfully advances _head to 1: ABA.
        //

        public bool LocalPop(out T item) {
            if (fields.Tail - fields.Head == 0) {
                item = null;
                return false;
            }

            do {

                //
                // The order of the two following instructions is strict. If the 
                // instructions were swapped the following execution is possible: 
                //      1) The owner thread sees *oldHead* == n;
                //      2) A stealer sees *head* == n + 1 and *tail* == n + 2
                //         and advances *head* to n + 2: the queue is now empty;
                //      3) The owner thread sees *tail* == n + 2 and decrements it to n + 1;
                //      4) Since size == tail - head == n + 1 - n == 1, the function succeeds.
                //
                // (This would not happen if size == 0 due to the 1 element in queue special case.)
                //
                // Also, a full-fence is required because of the release followed by acquire hazard. 
                //

                int tail = Interlocked.Decrement(ref fields.Tail);
                int oldHead = fields.Head; 

                int size = tail - oldHead;

                //
                // The queue was empty, so we correct *tail*.
                //

                if (size < 0) {
                    fields.Tail = oldHead;
                    item = null;
                    return false;
                }

                int index = tail & mask;
                item = items[index];

                if (size > 0) {
                    if (item != null) {
                        items[index] = null;
                        return true;
                    }
                    continue;
                }

                //
                // The queue has one element only (or none, if *head* has been updated). 
                //

                if (Interlocked.CompareExchange(ref fields.Head, oldHead + 1, oldHead) != oldHead) {
                    item = null;
                }

                //      
                // *head* is now greater than *tail*, meaning that the queue is empty,
                // so we correct tail.
                //

                fields.Tail = oldHead + 1;
                items[index] = null;
                return item != null;
            } while (true);
        }

        public bool TrySteal(out T item, ref bool missedSteal) {
            do {
                int head = fields.Head;
                int oldTail = fields.Tail;

                var items = this.items;
                int mask = items.Length - 1;

                int size = oldTail - head;

                if (size < -1) {

                    //
                    // Help the local thread to trim the indexes.
                    //

                    Interlocked.CompareExchange(ref fields.Head, head & mask, head);
                    continue;
                } 
                
                if (size <= 0) {
                    item = null;
                    return false;
                }
                
                item = items[head & mask];

                if (item == null && searchInProgress == head) {

                    //
                    // The local thread will remove the item and advance the *head*.
                    //
										
                    continue;
                }

                //
                // The following CAS promotes the ABA problem, since the indexes may have
                // overflown and item may not be the current object in position *head*. 
                //

                if (Interlocked.CompareExchange(ref fields.Head, head + 1, head) == head) {
                    
                    //
                    // We advanced *head*. If the item is null, try again.
                    //

                    if (item == null) {
                        continue;
                    }

                    //
                    // We can't set the item's position to null because the *head*
                    // we saw may have already been overwritten by a local push.
                    //

                    return true;
                }

                missedSteal = true;
                return false;
            } while (true);
        }
    }
}