using InfoPanel.Plugins;

namespace InfoPanel.TestPlugin2
{
    public class TestPlugin2 : BasePlugin
    {
        public TestPlugin2(string id, string name, string description) : base(id, name, description)
        {
        }

        public override string? ConfigFilePath => throw new NotImplementedException();

        public override TimeSpan UpdateInterval => throw new NotImplementedException();

        public override void Close()
        {
            throw new NotImplementedException();
        }

        public override void Initialize()
        {
            throw new NotImplementedException();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            throw new NotImplementedException();
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
