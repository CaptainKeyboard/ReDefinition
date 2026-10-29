using System;
using System.Collections.Generic;

using UnityEngine;

namespace ReDefinition.Landing
{
    // The object pools MechJeb's simulation borrows its objects from. It takes them from the pool, and gives them back when it is done, so that
    // a simulation run every few frames makes no garbage. Here the same, written for this code: a stack of the objects given back.
    public sealed class Pool<T> where T : class
    {
        private readonly Stack<T> free = new Stack<T>();
        private readonly Func<T> create;
        private readonly Action<T> reset;

        public Pool(Func<T> create, Action<T> reset)
        {
            this.create = create;
            this.reset = reset;
        }

        public int Size
        {
            get
            {
                lock (free) return free.Count;
            }
        }

        public T Borrow()
        {
            lock (free)
            {
                if (free.Count > 0) return free.Pop();
            }
            return create();
        }

        public void Release(T item)
        {
            reset(item);
            lock (free) free.Push(item);
        }
    }

    // Lists to borrow, for the points of a trajectory.
    public sealed class ListPool<T>
    {
        public static readonly ListPool<T> Instance = new ListPool<T>();

        private readonly Stack<List<T>> free = new Stack<List<T>>();

        public List<T> Borrow()
        {
            lock (free)
            {
                if (free.Count > 0) return free.Pop();
            }
            return new List<T>();
        }

        public void Release(List<T> list)
        {
            list.Clear();
            lock (free) free.Push(list);
        }

        public Disposable<List<T>> BorrowDisposable()
        {
            return new Disposable<List<T>>(Borrow(), this);
        }
    }

    // A borrowed list that goes back to its pool when it is disposed.
    public struct Disposable<T> : IDisposable where T : class
    {
        public readonly T value;
        private readonly object pool;

        public Disposable(T value, object pool)
        {
            this.value = value;
            this.pool = pool;
        }

        public void Dispose()
        {
            var lists = pool as ListPool<Vector3d>;
            var list = value as List<Vector3d>;
            if (lists != null && list != null) lists.Release(list);
        }
    }
}
