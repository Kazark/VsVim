using Microsoft.FSharp.Primitives.Basics;
using Moq;
using Vim.Interpreter;
using Xunit;

namespace Vim.UnitTest
{
    public sealed class VimScriptFunctionsTest
    {
        private readonly Mock<IBuiltinFunctionCaller> _builtinFunctionCaller;
        private readonly TestableStatusUtil _statusUtil;
        private readonly VimScriptFunctionCaller _callerUnderTest;

        public VimScriptFunctionsTest()
        {
            _statusUtil = new TestableStatusUtil();
            _builtinFunctionCaller = new Mock<IBuiltinFunctionCaller>(MockBehavior.Strict);
            _callerUnderTest = new VimScriptFunctionCaller(_builtinFunctionCaller.Object, _statusUtil);
        }

        private VariableValue Call(string functionName, VariableValue[] args)
        {
            var name = new VariableName(NameScope.Global, functionName);
            return _callerUnderTest.Call(name, List.ofArray(args));
        }

        private void CallExpectingError(string functionName, VariableValue[] args)
        {
            var result = Call(functionName, args);
            Assert.True(result.IsError);
            Assert.NotEmpty(_statusUtil.LastError);
        }

        [Fact]
        public void Exists_function_happy_path()
        {
            _builtinFunctionCaller.Setup(x => x.Call(It.IsAny<BuiltinFunctionCall.Exists>()))
                                  .Returns(VariableValue.NewNumber(0))
                                  .Verifiable();

            var result = Call("exists", new [] { VariableValue.NewString("x") });

            Assert.False(result.IsError);
            _builtinFunctionCaller.VerifyAll();
        }

        [Fact]
        public void Zero_is_not_enough_arguments_for_exists_function()
        {
            CallExpectingError("exists", new VariableValue[]{});
        }

        [Fact]
        public void Two_is_too_many_arguments_for_exists_function()
        {
            CallExpectingError("exists", new []
            {
                VariableValue.NewString("x"),
                VariableValue.NewString("y")
            });
        }
    }
}
