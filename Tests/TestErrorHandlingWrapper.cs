using System;
using System.Threading.Tasks;
using BtmI2p.JsonRpcHelpers.Client;
using Xunit;

namespace BtmI2p.JsonRpcHelpers.Tests
{
    public class TestErrorHandlingWrapperService
    {
        public void TestException()
        {
            throw new Exception("Test");
        }

        public int TestException1()
        {
            throw new Exception("Test1");
        }

        public async Task TestException2()
        {
            await Task.Delay(1000).ConfigureAwait(false);
            throw new Exception("Test2");
        }

        public virtual async Task<int> TestException3()
        {
            await Task.Delay(1000).ConfigureAwait(false);
            throw new Exception("Test3");
        }
    }
    public class TestErrorHandlingWrapper
    {
        [Fact]
        public async Task Test1()
        {
            var wrapper = new WrapperErrorHandling<TestErrorHandlingWrapperService>(
                new WrapperErrorHandlingSettings()
                {
                    AdvancedErrorHandling = (x, y) => { throw y; }
                },
                new TestErrorHandlingWrapperService()
            );
            var proxy = wrapper.GetProxy();
            //
            try
            {
                proxy.TestException();
            }
            catch (Exception exc)
            {
                if(exc.GetType() != typeof(Exception) || exc.Message != "Test")
                    throw new InvalidOperationException("Test");
            }
            //
            try
            {
                proxy.TestException1();
            }
            catch (Exception exc)
            {
                if (exc.GetType() != typeof(Exception) || exc.Message != "Test1")
                    throw new InvalidOperationException("Test1");
            }
            //
            try
            {
                await proxy.TestException2().ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                if (exc.GetType() != typeof(Exception) || exc.Message != "Test2")
                    throw new InvalidOperationException("Test2");
            }
            //
            try
            {
                await proxy.TestException3().ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                if (exc.GetType() != typeof(Exception) || exc.Message != "Test3")
                    throw new InvalidOperationException("Test3");
            }
        }
    }
}
