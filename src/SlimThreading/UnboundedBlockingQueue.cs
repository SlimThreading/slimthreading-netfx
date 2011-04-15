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
using System.Collections.Concurrent;

#pragma warning disable 0420

namespace SlimThreading {

    //
    // This class implements a generic unbounded blocking queue.
    //

    public class StUnboundedBlockingQueue<T> : StBlockingQueue<T> {

        //
        // The concurrent queue of data items.
        //

        private readonly ConcurrentQueue<T> dataQueue;

        //
        // The queue of thread waiting to take.
        //

        private readonly NonBlockingWaitQueue waitQueue;

        //
        // Constructors.
        //

        public StUnboundedBlockingQueue(bool lifoQueue)  {
            dataQueue = new ConcurrentQueue<T>();
            if (lifoQueue) {
                waitQueue = new NonBlockingLifoWaitQueue();
            } else {
                waitQueue = new NonBlockingFifoWaitQueue();
            }
        }

        public StUnboundedBlockingQueue() : this(false) { }

        //
        // Tries to release a waiter thread with a data item
        // retrieved from the data queue.
        //
    
        private void TryReleaseTakeWaiter() {
            do {
                T di;

                //
                // Try to dequeue a data item; if the queue is empty, return.
                //

                if (!dataQueue.TryDequeue(out di)) {
                    return;
                }

                //
                // Try to dequeue and lock a wait node.
                //

                WaitNode w;
                if ((w = waitQueue.TryDequeueAndLock()) != null) {

                    //
                    // Pass the data item to the waiter through its wait block,
                    // and unpark it.
                    //

                    w.channel = di;
                    w.parker.Unpark(w.waitKey);
                    return;
                }
            
                //
                // The wait queue is empty, so return the data item
                // to the data queue and retry the release, if the wait queue
                // seems non-empty.
                //

                dataQueue.Enqueue(di);
            } while (!waitQueue.IsEmpty);
        }
    
        //
        // Worker method that adds a data item in the blocking queue.
        //

        internal void AddWorker(T di) {

            //
            // First, try to dequeue and lock a wait node.
            //

            WaitNode w;
            if ((w = waitQueue.TryDequeueAndLock()) != null) {

                //
                // Pass the data item directly to the waiter through its
                // wait block and unpark it.
                //

                w.channel = di;
                w.parker.Unpark(w.waitKey);
            } else {

                //
                // The wait queue is empty. So, add the data item to the data
                // queue and recheck the wait queue. If, now, the wait
                // queue is non-empty, try to release a waiter thread.
                //

                dataQueue.Enqueue(di);
                if (!waitQueue.IsEmpty) {
                    TryReleaseTakeWaiter();
                }
            }
        }


        //
        // Tries to add immediately a data item to the queue.
        //

        public override bool TryAdd(T di) {
            AddWorker(di);
            return true;
        }

        //
        // Executes the prologue of the TryAdd operation.
        //

        internal override WaitNode TryAddPrologue(StParker pk, int key, T di, ref WaitNode ignored) {
            if (pk.TryLock()) {
                pk.UnparkSelf(key);
            }
            AddWorker(di);
            return null;
        }
      
        //
        // Tries to take immediately a data item from the queue.
        //

        public override bool TryTake(out T di) {
            return dataQueue.TryDequeue(out di);
        }

        //
        // Executes the prologue of the TryTake operation.
        //

        internal override WaitNode TryTakePrologue(StParker pk, int key, out T di, ref WaitNode hint) {
            if (dataQueue.TryDequeue(out di)) {

                if (pk.TryLock()) {
                    pk.UnparkSelf(key);
                } else {

                    //
                    // ...
                    //

                    AddWorker(di);
                }
                return null;
            }

            //
            // There are no data items available on the queue, ...
            //

            WaitNode wn = new WaitNode(pk, key);
            hint = waitQueue.Enqueue(wn);

            //
            // Since that a data item could have arrived after we check
            // the data queue but before we inserted our wait block in
            // the wait queue, we must retry to dequeue a data item from
            // the data queue if the parker is still unlocked.
            //
        
            if (!pk.IsLocked && dataQueue.TryDequeue(out di)) {
        
                //
                // We got a data item, so try to lock the parker and, if succeed,
                // unlink our wait node from the wait queue and self unpark the
                // current thread.
                //

                if (pk.TryLock()) {
                    waitQueue.Unlink(wn, hint);
                    pk.UnparkSelf(key);
                    return null;
                }

                //
                // If the parker is locked, someone else will give as a data
                // item. So, return the data item retrieved from the data queue,
                // undoing the previous take.
                //

                AddWorker(di);
            }

            //
            // Return the wait node inserted in the wait queue.
            //
        
            return wn;
        }

        //
        // Cancels the specified take attempt.
        //

        internal override void CancelTakeAttempt(WaitNode wn, WaitNode hint) {
            waitQueue.Unlink(wn, hint);
        }
    }
}
