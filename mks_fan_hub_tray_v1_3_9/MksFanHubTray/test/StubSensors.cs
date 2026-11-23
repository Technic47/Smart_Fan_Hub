using System.Collections.Generic;
namespace MksFanHub
{
    // Заглушка вместо LhmSensorSource для проверки сборки под Mono
    public class LhmSensorSource : ISensorSource
    {
        public string Info { get { return "stub"; } }
        public Dictionary<string, float> Read()
        {
            Dictionary<string, float> d = new Dictionary<string, float>();
            d["P102"] = 45.5f; d["CPU"] = 38f;
            return d;
        }
        public int Resets;
        public void Reset() { Resets++; }
        public void Close() { }
    }
}
