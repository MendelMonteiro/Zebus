using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Abc.Zebus.Routing;

namespace Abc.Zebus.Directory
{
    public class PeerSubscriptionTree
    {
        private readonly SubscriptionNode _rootNode = new SubscriptionNode(0, false);
        private List<Peer> _peersMatchingAllMessages = new List<Peer>();

        public bool IsEmpty => _rootNode.IsEmpty && _peersMatchingAllMessages.Count == 0;

        public void Add(Peer peer, BindingKey subscription)
        {
            UpdatePeerSubscription(peer, subscription, UpdateAction.Add);
        }

        public void Remove(Peer peer, BindingKey subscription)
        {
            UpdatePeerSubscription(peer, subscription, UpdateAction.Remove);
        }

        public IList<Peer> GetPeers(BindingKey routingKey)
        {
            var peerCollector = new PeerCollector(_peersMatchingAllMessages);

            _rootNode.Accept(peerCollector, routingKey);

            return peerCollector.GetPeers();
        }

        private void UpdatePeerSubscription(Peer peer, BindingKey subscription, UpdateAction action)
        {
            if (subscription.IsEmpty)
                UpdatePeersMatchingAllMessages(peer, action);
            else
                _rootNode.Update(peer, subscription, action);
        }

        private void UpdatePeersMatchingAllMessages(Peer peer, UpdateAction action)
        {
            UpdateList(ref _peersMatchingAllMessages, peer, action);
        }

        private static void UpdateList(ref List<Peer> peers, Peer peer, UpdateAction action)
        {
            var newPeers = new List<Peer>(peers.Capacity);
            newPeers.AddRange(peers.Where(x => x.Id != peer.Id));

            if (action == UpdateAction.Add)
                newPeers.Add(peer);

            peers = newPeers;
        }

        private class PeerCollector
        {
            private readonly Dictionary<PeerId, Peer> _collectedPeers = new Dictionary<PeerId, Peer>();
            private readonly List<Peer> _initialPeers;

            public PeerCollector(List<Peer> initialPeers)
            {
                _initialPeers = initialPeers;
            }

            public void Offer(IEnumerable<Peer> peers)
            {
                foreach (var peer in peers)
                {
                    _collectedPeers[peer.Id] = peer;
                }
            }

            public List<Peer> GetPeers()
            {
                if (_collectedPeers.Count == 0)
                    return _initialPeers;

                Offer(_initialPeers);

                return _collectedPeers.Values.ToList();
            }
        }

        private class SubscriptionNode
        {
            private static readonly Action<SubscriptionNode, string> _removeNode = (x, part) => x.RemoveChildNode(part);
            private static readonly Action<SubscriptionNode, string> _removeSharpNode = (x, _) => x._sharpNode = null;
            private static readonly Action<SubscriptionNode, string> _removeStarNode = (x, _) => x._starNode = null;

            private readonly Func<string, SubscriptionNode> _createChildNode;
            private readonly int _nextPartIndex;
            private readonly bool _matchesAll;
            private ConcurrentDictionary<string, SubscriptionNode> _childrenNodes;
            private List<Peer> _peers = new List<Peer>();
            private SubscriptionNode _sharpNode;
            private SubscriptionNode _starNode;
            private int _peerCountIncludingChildren;

            public SubscriptionNode(int nextPartIndex, bool matchesAll)
            {
                _nextPartIndex = nextPartIndex;
                _matchesAll = matchesAll;
                _createChildNode = _ => new SubscriptionNode(_nextPartIndex + 1, false);
            }

            public bool IsEmpty => _peerCountIncludingChildren == 0;

            public void Accept(PeerCollector peerCollector, BindingKey routingKey)
            {
                if (IsLeaf(routingKey) || _matchesAll)
                {
                    peerCollector.Offer(_peers);
                    return;
                }

                _sharpNode?.Accept(peerCollector, routingKey);
                _starNode?.Accept(peerCollector, routingKey);

                var nextPart = routingKey.GetPart(_nextPartIndex);
                if (nextPart == null)
                    return;

                if (_childrenNodes == null)
                    return;

                SubscriptionNode childNode;
                if (_childrenNodes.TryGetValue(nextPart, out childNode))
                    childNode.Accept(peerCollector, routingKey);
            }

            public int Update(Peer peer, BindingKey subscription, UpdateAction action)
            {
                if (IsLeaf(subscription))
                {
                    var update = UpdateList(peer, action);
                    _peerCountIncludingChildren += update;

                    return update;
                }

                var nextPart = subscription.GetPart(_nextPartIndex);

                if (subscription.IsSharp(_nextPartIndex) || nextPart == null)
                {
                    var sharpNode = GetOrCreateSharpNode();
                    return UpdateChildNode(sharpNode, peer, subscription, action, null, _removeSharpNode);
                }

                if (subscription.IsStar(_nextPartIndex))
                {
                    var starNode = GetOrCreateStarNode();
                    return UpdateChildNode(starNode, peer, subscription, action, null, _removeStarNode);
                }

                var childNode = GetOrAddChildNode(nextPart);
                return UpdateChildNode(childNode, peer, subscription, action, nextPart, _removeNode);
            }

            private int UpdateChildNode(SubscriptionNode childNode, Peer peer, BindingKey subscription, UpdateAction action, string childNodePart, Action<SubscriptionNode, string> remover)
            {
                var update = childNode.Update(peer, subscription, action);
                _peerCountIncludingChildren += update;
                
                if (childNode.IsEmpty)
                    remover(this, childNodePart);

                return update;
            }

            private bool IsLeaf(BindingKey bindingKey)
            {
                if (_nextPartIndex == 0)
                    return false;

                if (bindingKey.IsEmpty)
                    return _nextPartIndex == 1;

                return _nextPartIndex == bindingKey.PartCount;
            }

            private SubscriptionNode GetOrAddChildNode(string part)
            {
                if (_childrenNodes == null)
                    _childrenNodes = new ConcurrentDictionary<string, SubscriptionNode>();

                return _childrenNodes.GetOrAdd(part, _createChildNode);
            }

            private void RemoveChildNode(string part)
            {
                if (_childrenNodes == null)
                    return;

                SubscriptionNode node;
                _childrenNodes.TryRemove(part, out node);
            }

            private SubscriptionNode GetOrCreateSharpNode()
            {
                return _sharpNode ?? (_sharpNode = new SubscriptionNode(_nextPartIndex + 1, true));
            }

            private SubscriptionNode GetOrCreateStarNode()
            {
                return _starNode ?? (_starNode = new SubscriptionNode(_nextPartIndex + 1, false));
            }

            private int UpdateList(Peer peer, UpdateAction action)
            {
                return action == UpdateAction.Add ? AddToList(peer) : RemoveFromList(peer);
            }

            private int AddToList(Peer peerToAdd)
            {
                var removed = false;
                var newPeers = new List<Peer>(_peers.Capacity);
                foreach (var peer in _peers)
                {
                    if (peer.Id == peerToAdd.Id)
                        removed = true;
                    else
                        newPeers.Add(peer);
                }
                newPeers.Add(peerToAdd);

                _peers = newPeers;

                return removed ? 0 : 1;
            }

            private int RemoveFromList(Peer peerToRemove)
            {
                var removed = false;
                var newPeers = new List<Peer>(_peers.Capacity);
                foreach (var peer in _peers)
                {
                    if (peer.Id == peerToRemove.Id)
                        removed = true;
                    else
                        newPeers.Add(peer);
                }

                _peers = newPeers;

                return removed ? -1 : 0;
            }
        }

        private enum UpdateAction
        {
            Add,
            Remove,
        }
    }
}