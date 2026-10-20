using System;
using System.Runtime.CompilerServices;
using FadeBasic.Virtual;

namespace FadeBasic.Compiled
{
    /// <summary>
    /// The compiled-backend form of a Fade array. It mirrors the VM's layout: one flat,
    /// row-major block plus a size and a "scaler" per rank (the VM keeps those in
    /// registers next to the array pointer). Keeping them separate from <see cref="data"/>
    /// matters, because Fade's `a = b` array assignment copies only the data block and
    /// leaves the target's rank registers alone.
    /// </summary>
    public sealed class FadeArray<T>
    {
        public T[] data;
        public int n0, n1, n2, n3, n4; // rank sizes
        public int m0, m1, m2, m3, m4; // rank scalers
        public readonly int rank;

        public FadeArray(int rank)
        {
            this.rank = rank;
            data = Array.Empty<T>();
        }

        public int Length => data.Length;

        public int GetSize(int r)
        {
            switch (r)
            {
                case 0: return n0;
                case 1: return n1;
                case 2: return n2;
                case 3: return n3;
                case 4: return n4;
                default: throw new IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// `dim` / `redim`: the VM recomputes every rank register and allocates a fresh
        /// block. Contents are never preserved.
        /// </summary>
        public void Dim(int s0, int s1 = 0, int s2 = 0, int s3 = 0, int s4 = 0)
        {
            n0 = s0; n1 = s1; n2 = s2; n3 = s3; n4 = s4;
            // scaler(last) = 1, scaler(i) = size(i+1) * scaler(i+1)
            switch (rank)
            {
                case 1: m0 = 1; break;
                case 2: m1 = 1; m0 = n1; break;
                case 3: m2 = 1; m1 = n2; m0 = n1 * m1; break;
                case 4: m3 = 1; m2 = n3; m1 = n2 * m2; m0 = n1 * m1; break;
                case 5: m4 = 1; m3 = n4; m2 = n3 * m3; m1 = n2 * m2; m0 = n1 * m1; break;
            }

            long total = 1;
            switch (rank)
            {
                case 5: total *= n4; goto case 4;
                case 4: total *= n3; goto case 3;
                case 3: total *= n2; goto case 2;
                case 2: total *= n1; goto case 1;
                case 1: total *= n0; break;
            }
            data = new T[checked((int)total)];
        }

        /// <summary>`a = b` between arrays: copy b's block, keep a's rank registers.</summary>
        public void AssignFrom(FadeArray<T> other)
        {
            data = (T[])other.data.Clone();
        }

        public FadeArray<T> CloneArray()
        {
            var copy = (FadeArray<T>)MemberwiseClone();
            copy.data = (T[])data.Clone();
            return copy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Check(int index, int size, int site)
        {
            if ((uint)index >= (uint)size) // index < 0 || index >= size
            {
                throw FadeRt.InvalidAddress(index, size, site);
            }
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Idx(int i0, int site)
        {
            return Check(i0, n0, site) * m0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Idx(int i0, int i1, int site)
        {
            return Check(i0, n0, site) * m0 + Check(i1, n1, site) * m1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Idx(int i0, int i1, int i2, int site)
        {
            return Check(i0, n0, site) * m0 + Check(i1, n1, site) * m1 + Check(i2, n2, site) * m2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Idx(int i0, int i1, int i2, int i3, int site)
        {
            return Check(i0, n0, site) * m0 + Check(i1, n1, site) * m1 + Check(i2, n2, site) * m2 +
                   Check(i3, n3, site) * m3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Idx(int i0, int i1, int i2, int i3, int i4, int site)
        {
            return Check(i0, n0, site) * m0 + Check(i1, n1, site) * m1 + Check(i2, n2, site) * m2 +
                   Check(i3, n3, site) * m3 + Check(i4, n4, site) * m4;
        }

        /// <summary>A 1-rank array holding these values (a mock body's `params` argument).</summary>
        public static FadeArray<T> FromValues(T[] values)
        {
            var array = new FadeArray<T>(1);
            array.Dim(values?.Length ?? 0);
            if (values != null) Array.Copy(values, array.data, values.Length);
            return array;
        }

        /// <summary>`params` spread of a 1-rank array into a command.</summary>
        public T[] Spread() => (T[])data.Clone();
    }
}
