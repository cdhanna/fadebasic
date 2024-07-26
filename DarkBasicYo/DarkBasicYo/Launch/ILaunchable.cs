using System.Collections.Generic;
using DarkBasicYo.Virtual;

namespace DarkBasicYo.Launch
{
    public interface ILaunchable
    {
        public byte[] Bytecode { get; }
        public CommandCollection CommandCollection { get; }
    }
}