using System.Linq;
using System.Threading.Tasks;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.MiscUtils;
using Xunit;
using Xunit.Abstractions;

namespace BtmI2p.JsonRpcHelpers.Tests
{
    public interface ITestJsonRpcInterface1
    {
        Task<int> Add(int a, int b);
        void Any();
    }

    public interface ITestJsonRpcInterface2
        : ITestJsonRpcInterface1
    {
        int Mul(int a, int b);
    }

    public class TestJsonPpcImpl : ITestJsonRpcInterface2
    {
        public async Task<int> Add(int a, int b)
        {
            return await Task.FromResult(a + b).ConfigureAwait(false);
        }

        public void Any()
        {
            return;
        }

        public int Mul(int a, int b)
        {
            return a*b;
        }
    }

    public class TestJsonRpc
    {
        private readonly ITestOutputHelper _output;
        public TestJsonRpc(ITestOutputHelper output)
        {
	        _output = output;
        }
        [Fact]
        public void TestsJsonRpcServerMethodInfo()
        {
            var x = JsonRpcServerMethodInfo
                .GetMethodInfos(typeof(ITestJsonRpcInterface2));
            _output.WriteLine(x.WriteObjectToJson());
            _output.WriteLine("##########################");
            var y = JsonRpcServerMethodInfo
                .GetMethodInfos(typeof(TestJsonPpcImpl));
            _output.WriteLine(y.WriteObjectToJson());
        }
        [Fact]
        public void TestEqualJsonRpcServerMethodInfo()
        {
            Assert.True(
                JsonRpcClientProcessor.CheckRpcServerMethodInfos(
                    typeof(ITestJsonRpcInterface2),
                    JsonRpcServerMethodInfo
                        .GetMethodInfos(typeof(ITestJsonRpcInterface2)).ToList()
                )
            );
        }
    }
}
