using System.Collections.Generic;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public interface ILaunchable
    {
        public byte[] Bytecode { get; }
        public CommandCollection CommandCollection { get; }
    }
}