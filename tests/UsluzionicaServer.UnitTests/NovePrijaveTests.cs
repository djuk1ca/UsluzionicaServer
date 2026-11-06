using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Podnaslov u traci „Za tebe" („3 nove prijave"). Srpska množina zavisi od
/// poslednje cifre, a 11–14 su izuzetak — lako se omakne.
/// </summary>
public class NovePrijaveTests
{
    [Theory]
    [InlineData(1,   "1 nova prijava")]
    [InlineData(2,   "2 nove prijave")]
    [InlineData(4,   "4 nove prijave")]
    [InlineData(5,   "5 novih prijava")]
    [InlineData(11,  "11 novih prijava")]
    [InlineData(12,  "12 novih prijava")]
    [InlineData(14,  "14 novih prijava")]
    [InlineData(21,  "21 nova prijava")]
    [InlineData(22,  "22 nove prijave")]
    [InlineData(111, "111 novih prijava")]
    public void Mnozina(int n, string ocekivano) =>
        JobService.NovePrijave(n).Should().Be(ocekivano);
}
