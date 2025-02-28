using System.Text;
using FadeBasic.Virtual;

namespace Tests;

public partial class TokenVm
{

    
    [Test]
    public void BigSrc_LotsOfVariables()
    {
        var sb = new StringBuilder();
        var max = ulong.MaxValue - 100
        for (var i = 0; i < max; i++)
        {
            sb.AppendLine($"x_{i} = {i}");
        }

        var src = sb.ToString();
        Setup(src, out var compiler, out var prog);
        var vm = new VirtualMachine(prog);
        vm.hostMethods = compiler.methodTable;
        vm.Execute2(0);

        for (var i = 0; i < max; i++)
        {
            Assert.That(vm.dataRegisters[i], Is.EqualTo(i), $"variable at reg {i} is wrong.");

        }
    }
    
}