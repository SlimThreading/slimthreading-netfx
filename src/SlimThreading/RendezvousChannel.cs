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
    // This value type is used to identify a rendezvous between
    // a sender and a receiver.
    //

    public struct StRendezvousToken {
        internal object sender;

        internal StRendezvousToken(object s) {
            sender = s;
        }

        public bool ExpectsReply {
            get { return sender != null; }
        }
    }

	//
	// This class implements a rendezvous channel.
	//

	public class StRendezvousChannel<T,R> {
		
		//
		// Types of wait nodes used by the rendezvous channel.
		//

        private enum ReqType { Sentinel, SendOnly, SendWaitReply, Receive };

        //
        // The base class of the wait nodes.
        //

        private class WaitNode : StParker {

            //
            // The link field and the type of request.
            //

            internal volatile WaitNode next;
            internal readonly ReqType type;

            //
            // Constructors.
            //

            internal WaitNode(ReqType t) { type = t; }

            internal WaitNode() { type = ReqType.Sentinel; }

            //
            // CASes the *next* field.
            //

            internal bool CasNext(WaitNode n, WaitNode nn) {
                return (next == n && Interlocked.CompareExchange<WaitNode>(ref next, nn, n) == n);
            }
        }

        //
        // WaitOne nodes used by the sender threads.
        //

        private class SendWaitNode<Q, P> : WaitNode {
            internal Q request;
            internal P response;

            //
            // Constructors.
            //

            internal SendWaitNode(ReqType t, Q request) : base(t) {
                this.request = request;
            }

            internal SendWaitNode(ReqType t) : base(t) {}
        }

        //
        // WaitOne nodes used by the receiver threads.
        //

        private class RecvWaitNode<Q, P> : WaitNode {
            internal Q request;
            internal SendWaitNode<Q,P> sender;

            //
            // Constructor.
            //

            internal RecvWaitNode() : base(ReqType.Receive) {}
        }
	
		//
		// Internal API used with Fifo and Lifo implementations of
        // the rendezvous channel.
		//

		private abstract class RendezvousChannel {

			//
			// Sends a message through the channel.
			//

			internal abstract bool Send(ReqType type, T request, out R response,
                                        StCancelArgs cargs);
	
			//
			// Receives a message through the channel.
			//

			internal abstract bool Receive(out T request, out StRendezvousToken token,
                                           StCancelArgs cargs);
		}
	
		//
		// This class implements a rendezvous channels where sends and
        // reveices are serviced by last-in-first-out order.
		//
	
		private sealed class LifoRendezvousChannel : RendezvousChannel {

			//
			// The wait list is a non-blocking stack.
			//
	
			private volatile WaitNode top;

            //
            // Constructor.
            //

            internal LifoRendezvousChannel() {}

			//
			// Sends a message, activating the specified cancellers.
			//

            internal override bool Send(ReqType type, T request, out R response,
                                        StCancelArgs cargs) {
				SendWaitNode<T,R> wn = null;
                bool enableCancel = true;

                do {
                    WaitNode t;
					if ((t = top) == null || t.type != ReqType.Receive) {

						//
						// The stack is empty or the first wait node belongs
                        // to a sender thread.
                        //

                        if (wn == null) {
                            if (cargs.Timeout == 0) {
                                response = default(R);
                                return false;
                            }
                            wn = new SendWaitNode<T,R>(type, request);
                        }
						wn.next = t;
                        if (Interlocked.CompareExchange<WaitNode>(ref top, wn, t) == t) {
                            break;
                        }
					} else {

						//
						// The top wait node belongs to a receiver thread;
                        // so, try to pop it from the stack.
						//

                        if (Interlocked.CompareExchange<WaitNode>(ref top, t.next, t) == t) {

                            //
                            // Try to lock the associated parker and, if succeed, initiate
                            // the rendezvous with its owner thread.
                            //

							if (t.TryLock()) {
                                RecvWaitNode<T,R> rwn = (RecvWaitNode<T,R>)t;
                                rwn.request = request;
                                if (type == ReqType.SendOnly) {
                                    rwn.sender = null;
                                    rwn.Unpark(StParkStatus.Success);
                                    response = default(R);
                                    return true;
                                }
                                if (wn == null) {
                                    wn = new SendWaitNode<T,R>(ReqType.SendWaitReply);
                                }

                                rwn.sender = wn;
                                wn.SelfCancel();
                                rwn.Unpark(StParkStatus.Success);
                                enableCancel = false;
                                break;
							}
						}
                    }
                } while (true);

                //
                // Park the current thread, activating the specified cancellers,
                // but only if the rendezvous wasn't initiated.
                //

                int ws;
                if (enableCancel) {
                    ws = wn.Park(cargs);
                } else {
                    wn.Park();
                    ws = StParkStatus.Success;
                }

                //
                // If succeed, retrive the response and return success.
                //

                if (ws == StParkStatus.Success) {
                    response = wn.response;
                    return true;
                }
                
                //
                // The send was cancelled; so, unlink the wait node from the wait
                // queue and report the failure appropriately.
                //

                Unlink(wn);
                response = default(R);
                StCancelArgs.ThrowIfException(ws);
                return false;
			}

			//
			// Receives a message, activating the specified cancellers.
			//

            internal override bool Receive(out T request, out StRendezvousToken token,
                                           StCancelArgs cargs) {
                RecvWaitNode<T,R> wn = null;
                do {
                    WaitNode t = top;
                    if (t == null || t.type == ReqType.Receive) {

						//
						// The stack is empty or the wait note at the top of the
                        // stack belongs to a receiver thread. In this case, we must
                        // create a wait node and push it onto the stack.
						//

                        if (wn == null) {
                            if (cargs.Timeout == 0) {
                                request = default(T);
                                token = new StRendezvousToken(null);
                                return false;
                            }
                            wn = new RecvWaitNode<T,R>();
                        }
                        wn.next = t;
                        if (Interlocked.CompareExchange<WaitNode>(ref top, wn, t) == t) {
                            break;
						}
					} else {

						//
						// The top wait node belongs to a sender thread; so, try to
                        // pop it from the stack.
						//

                        if (Interlocked.CompareExchange<WaitNode>(ref top, t.next, t) == t) {

                            //
                            // Try to lock the associated parker and, if succeed, initiate
                            // the rendezvous with its owner thread.
                            //

                            if (t.TryLock()) {
                                SendWaitNode<T, R> swn = (SendWaitNode<T, R>)t;
                                request = swn.request;
                                if (swn.type == ReqType.SendOnly) {
                                    token = new StRendezvousToken(null);
                                    swn.Unpark(StParkStatus.Success);
                                } else {
                                    token = new StRendezvousToken(swn);
                                }
                                return true;
                            }
                        }
                    }
                } while (true);

                //
                // Park the current thread, activating the specified cancellers.
                //

                int ws = wn.Park(cargs);

                //
                // If succeed, retrive the request from the wait node, build a
                // rendezvous token and return success.
                //

                if (ws == StParkStatus.Success) {
                    request = wn.request;
                    token = new StRendezvousToken(wn.sender);
                    return true;
                }

                //
                // The receive was cancelled; so, unlink the wait node from
                // the wait queue and return the failure approriately.
                //

                Unlink(wn);
                request = default(T);
                token = new StRendezvousToken(null);
                StCancelArgs.ThrowIfException(ws);
                return false;
			}

            //
            // Unlinks the specified wait node from the stack.
            //

            private void Unlink(WaitNode wn) {
             
                //
                // Absorb the cancelled wait block at top of the stack.
                //

                WaitNode p;
                do {
                    if ((p = top) == null) {
                        return;
                    }
                    if (p.IsLocked) {
                        Interlocked.CompareExchange<WaitNode>(ref top, p.next, p);
                    } else {
                        break;
                    }   
                } while (true);

                //
                // Compute a wait node that follows "wn" and try
                // to unsplice the wait node.
                //

                WaitNode past;
                if ((past = wn.next) != null && past.IsLocked) {
                    past = past.next;
                }

                while (p != null && p != past) {
                    WaitNode n = p.next;
                    if (n != null && n.IsLocked) {
                        p.CasNext(n, n.next);
                    } else {
                        p = n;
                    }
                }
            }
		}

		//
		// This class implements a rendezvous channel where the sends and
        // receives are serviced by first-in-first-out order.
		//
	
		private sealed class FifoRendezvousChannel : RendezvousChannel {
 
			//
			// The wait list is a non-blocking queue.
			//
	
			private volatile WaitNode head;
			private volatile WaitNode tail;

            //
            // The predecessor of the wait node that must be unlinked
            // when the right conditions are meet.
            //

            private volatile WaitNode toUnlink;

			//
			// Constructor.
			//
		
			internal FifoRendezvousChannel() {
				head = tail = new WaitNode();
            }

            //
            // Advances the queue's head.
            //

            private bool AdvanceHead(WaitNode h, WaitNode nh) {
                if (head == h && Interlocked.CompareExchange<WaitNode>(ref head, nh, h) == h) {
                    h.next = h;     // mark the previous head wait node as unlinked.
                    return true;
                }
                return false;
            }

            //
            // Advances the queue's tail.
            //

            private bool AdvanceTail(WaitNode t, WaitNode nt) {
                return (tail == t && Interlocked.CompareExchange<WaitNode>(ref tail, nt, t) == t);
            }
        
            //
            // CASes on the *toUnlink" field.
            //
            
            private bool CasToUnlink(WaitNode tu, WaitNode ntu) {
                return (toUnlink == tu &&
                        Interlocked.CompareExchange<WaitNode>(ref toUnlink, ntu, tu) == tu);
            }
  
			//
			// Sends a message, activating the specified cancellers.
			//

            internal override bool Send(ReqType type, T request, out R response,
                                        StCancelArgs cargs) {
                SendWaitNode<T,R> wn = null;
                WaitNode pred = null;
                bool enableCancel = true;
				do {
					WaitNode h = head;
					WaitNode t = tail;
                    WaitNode hn;
					if ((hn = h.next) == null || hn.type != ReqType.Receive) {

						//
                        // The wait queue is empty or contains only sender
                        // wait nodes. So, do the consistency checks in order
                        // to insert our wait node.
						// 

						WaitNode tn;
						if ((tn = t.next) != null) {
							AdvanceTail(t, tn);
							continue;
						}

						//
						// Create a wait node, if we don't have one. However,
                        // if a null timeout was specified, return failure.
						//

						if (wn == null) {
                            if (cargs.Timeout == 0) {
                                response = default(R);
                                return false;
                            }
							wn = new SendWaitNode<T,R>(type, request);
						}

                        //
                        // Try to enqueue the wait node.
                        //

                        if (t.CasNext(null, wn)) {

                            //
                            // Set the new tail, save the predecessor wait node
                            // and break the loop.
                            //

                            AdvanceTail(t, wn);
                            pred = t;
                            break;
                        }
					} else {

						//
						// It seems that the wait node that is at front of the queue
                        // belongs to a receive thread; so, try to remove it and
                        // to initiate the rendezvous with the underlying thread.
                        //

                        if (AdvanceHead(h, hn)) {
						    if (hn.TryLock()) {

                                //
                                // We locked the receiver's wait node. So, get
                                // the request message.
                                //

                                RecvWaitNode<T,R> rwn = (RecvWaitNode<T,R>)hn;
                                rwn.request = request;

                                //
                                // If this a send only request, sets the sender to null,
                                // unpark the receiver thread and return success.
                                //

                                if (type == ReqType.SendOnly) {
                                    rwn.sender = null;
                                    rwn.Unpark(StParkStatus.Success);
                                    response = default(R);
                                    return true;
                                }

                                //
                                // This is a send and reply request. So, create a wait
                                // node in order to park the current thread and pass the
                                // wait node to the receiver through the rendezvous token.
                                //

                                if (wn == null) {
                                    wn = new SendWaitNode<T, R>(ReqType.SendWaitReply);
                                }
                                rwn.sender = wn;
                                wn.SelfCancel();

                                //
                                // Unpark the receiver thread and go to wait until
                                // reply. After the rendezvous is initiated the
                                // cancellers are deactivated.
                                //

                                rwn.Unpark(StParkStatus.Success);
                                enableCancel = false;
                                break;
                            }
                        }
					}
				} while (true);

                //
                // Park the current thread activating the specified cancellers,
                // if appropriate.
                //

                int ws;
                if (enableCancel) {
                    ws = wn.Park(cargs);
                } else {
                    wn.Park();
                    ws = StParkStatus.Success;
                }

                //
                // If succeed, retrieve the response from the wait node
                // and return success.
                // 

                if (ws == StParkStatus.Success) {
                    response = wn.response;
                    return true;
                }

                //
                // The send was cancelled; so unlink the wait node from
                // the wait queue and report the failure appropriately.
                //

                Unlink(wn, pred);
                response = default(R);
                StCancelArgs.ThrowIfException(ws);
                return false;
            }
		
			//
			// Receives a message, activativating the specified cancellers.
			//

            internal override bool Receive(out T request, out StRendezvousToken token,
                                           StCancelArgs cargs) {
                RecvWaitNode<T,R> wn = null;
                WaitNode pred;
				do {
					WaitNode h = head;
					WaitNode t = tail;
                    WaitNode hn;
					if ((hn = h.next) == null || hn.type == ReqType.Receive) {

						//
						// The wait queue is empty or contains only receiver
                        // wait nodes. So, do the consistency checks in order to
                        // insert our wait node.
						//

						WaitNode tn;
						if ((tn = t.next) != null) {
                            AdvanceTail(t, tn);
							continue;
						}

						//
						// Create a wait node, if we don't have one. However,
                        // return failure if a null timeout was specified.
						//

						if (wn == null) {
                            if (cargs.Timeout == 0) {
                                request = default(T);
                                token = new StRendezvousToken(null);
                                return false;
                            }
                            wn = new RecvWaitNode<T, R>();
                        }

                        //
                        // Try to enqueue the wait node.
                        //

                        if (t.CasNext(null, wn)) {
                            AdvanceTail(t, wn);
                            pred = t;
                            break;
                        }
					} else {

						//
						// There is a sender wait node at the front of the queue.
                        // So, try to remove it to initiate the rendezvous.
						//

                        if (AdvanceHead(h, hn)) {
                            if (hn.TryLock()) {
                                SendWaitNode<T,R> swn = (SendWaitNode<T,R>)hn;

                                //
                                // Get the request, then, If this is a send only
                                // request build a null rendezvous token and unpark
                                // the sender thread; otherwise, build a rendezvous token
                                // with the sender wait node.
                                //

                                request = swn.request;
                                if (swn.type == ReqType.SendOnly) {
                                    token = new StRendezvousToken(null);
                                    swn.Unpark(StParkStatus.Success);
                                } else {
                                    token = new StRendezvousToken(swn);
                                }
                                return true;
                            }
                        }
                    }
				} while (true);

                //
                // Park the current thread, activating the specified cancellers.
                //

                int ws = wn.Park(cargs);

                //
                // If succeed, retrive the request and the rendezvous token
                // from the wait node and return success.
                // 

                if (ws == StParkStatus.Success) {
                    request = wn.request;
                    token = new StRendezvousToken(wn.sender);
                    return true;
                }

                //
                // The receive was cancelled; so, unlink our wait node from
                // the wait queue and report the failure appropriately.
                //

                Unlink(wn, pred);
                request = default(T);
                token = default(StRendezvousToken);
                StCancelArgs.ThrowIfException(ws);
                return false;
            }

            //
            // Unlinks the specified wait node from the queue.
            //

            private void Unlink(WaitNode wn, WaitNode pred) {
                while (pred.next == wn) {

                    //
                    // Remove the cancelled wait nodes that are at front
                    // of the queue.
                    //


                    WaitNode h, hn;
                    if ((hn = (h = head).next) != null && hn.IsLocked) {
                        AdvanceHead(h, hn);
                        continue;
                    }

                    //
                    // If the queue is empty, return.
                    //

                    WaitNode t, tn;
                    if ((t = tail) == h) {
                        return;
                    }

                    //
                    // If the queue isn't in a quiescent state, retry.
                    //

                     if (t != tail) {
                        continue;
                    }
                    if ((tn = t.next) != null) {
                        AdvanceTail(t, tn);
                        continue;
                    }

                    //
                    // If the wait node isn't at tail of the queue, try to
                    // unlink it and return if succeed.
                    //

                    if (wn != t) {
                        WaitNode wnn;
                        if ((wnn = wn.next) == wn || pred.CasNext(wn, wnn)) {
                            return;
                        }
                    }

                    //
                    // Try unlinking previous cancelled wait block.
                    //
                    
                    WaitNode dp;
                    if ((dp = toUnlink) != null) {
                        WaitNode d, dn;
                        if ((d = dp.next) == dp ||
                            ((dn = d.next) != null && dp.CasNext(d, dn))) {
                            CasToUnlink(dp, null);
                        }
                        if (dp == pred) {
                            return;             // "wn" is an already saved node
                        }
                    } else if (CasToUnlink(null, pred)) {
                        return;
                    }
                }
            }
		}

		//
		// The rendezvous port implementation used by this instance.
		//
	
		private readonly RendezvousChannel channel;
	
		//
		// Constructors.
		//
 
		public StRendezvousChannel(bool fifoService) {
            if (fifoService) {
                channel = new FifoRendezvousChannel();
            } else {
                channel = new LifoRendezvousChannel();
            }
        }

        public StRendezvousChannel() : this(true) { }
	
		//
		// Sends the specified message through the channel waiting
        // until the message is received.
		//

        public bool Send(T message, StCancelArgs cargs) {
            R ignored;
            return channel.Send(ReqType.SendOnly, message, out ignored, cargs);
        }

        //
        // Sends the specified message and waits for the reply.
        //

        public bool SendWaitReply(T request, out R response, StCancelArgs cargs) {
            return channel.Send(ReqType.SendWaitReply, request, out response, cargs);
        }

        //
        // Receives a message through the rendezvous channel.
        //

        public bool Receive(out T request, out StRendezvousToken token, StCancelArgs cargs) {
            return channel.Receive(out request, out token, cargs);
        }

        //
        // Replies to the rendezvous identified by the specified token.
        //

        public void Reply(StRendezvousToken token, R response) {
            SendWaitNode<T,R> swn = token.sender as SendWaitNode<T,R>;
            if (swn == null) {
                throw new ArgumentException("token");
            }
            swn.response = response;
            swn.Unpark(StParkStatus.Success);
        }
	}
}
