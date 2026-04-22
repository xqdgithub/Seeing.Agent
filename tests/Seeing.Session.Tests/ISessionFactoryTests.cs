using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Seeing.Session.Tests
{
    public class ISessionFactoryTests
    {
        [Fact]
        public void Methods_HaveExpectedSignatures()
        {
            var iface = typeof(Seeing.Session.Core.ISessionFactory);

            // CreateAsync(string title = null, string partitionId = null, string agentId = null) : Task<ISession>
            var create = iface.GetMethod("CreateAsync");
            Assert.NotNull(create);
            Assert.Equal(typeof(Task<Seeing.Session.Core.ISession>), create.ReturnType);
            var createParams = create.GetParameters();
            Assert.Equal(3, createParams.Length);
            Assert.Equal(typeof(string), createParams[0].ParameterType);
            Assert.Equal(typeof(string), createParams[1].ParameterType);
            Assert.Equal(typeof(string), createParams[2].ParameterType);

            // CloneAsync(string sourceSessionId, string newSessionId) : Task<ISession>
            var clone = iface.GetMethod("CloneAsync");
            Assert.NotNull(clone);
            Assert.Equal(typeof(Task<Seeing.Session.Core.ISession>), clone.ReturnType);
            var cloneParams = clone.GetParameters();
            Assert.Equal(2, cloneParams.Length);
            Assert.Equal(typeof(string), cloneParams[0].ParameterType);
            Assert.Equal(typeof(string), cloneParams[1].ParameterType);
            Assert.Equal("sourceSessionId", cloneParams[0].Name);
            Assert.Equal("newSessionId", cloneParams[1].Name);

            // ResumeAsync(string sessionId) : Task<ISession>
            var resume = iface.GetMethod("ResumeAsync");
            Assert.NotNull(resume);
            Assert.Equal(typeof(Task<Seeing.Session.Core.ISession>), resume.ReturnType);
            var resumeParams = resume.GetParameters();
            Assert.Single(resumeParams);
            Assert.Equal(typeof(string), resumeParams[0].ParameterType);
            Assert.Equal("sessionId", resumeParams[0].Name);
        }
    }
}
