using Zenith.Core.Processes;
namespace Zenith.Tests;
public class ProcessKeyEncodingTests
{
    [Theory]
    [InlineData('\0', "F", 2, 'f')]
    [InlineData('\0', "Q", 3, 'q')]
    [InlineData('\u0006', "F", 2, 'f')]
    [InlineData('é', "E", 0, 'é')]
    [InlineData('€', "E", 6, '€')]
    [InlineData('\0', "Enter", 2, '\0')]
    [InlineData('\0', "F", 0, '\0')]
    [InlineData('\0', "F", 6, '\0')]
    public void PreservesTextAndSuppliesMissingControlLetter(char text, string key, int modifiers, char expected)
        => Assert.Equal(expected, ProcessKeyEncoding.Text(text, key, modifiers));
}
