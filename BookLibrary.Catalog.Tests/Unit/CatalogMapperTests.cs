using BookLibrary.Catalog.Domain;
using BookLibrary.Catalog.Mapping;

namespace BookLibrary.Catalog.Tests.Unit;

/// <summary>Verifies the Mapperly domain→contract mapping, in particular Guid→string ids.</summary>
public class CatalogMapperTests
{
    private readonly CatalogMapper _mapper = new();

    [Test]
    public async Task ToContract_WhenMappingBook_ShouldCopyFieldsAndStringifyId()
    {
        var id = Guid.NewGuid();
        var book = new Book { Id = id, Title = "Clean Code", Author = "Martin", PageCount = 464 };

        var contract = _mapper.ToContract(book);

        await Assert.That(contract.Id).IsEqualTo(id.ToString());
        await Assert.That(contract.Title).IsEqualTo("Clean Code");
        await Assert.That(contract.Author).IsEqualTo("Martin");
        await Assert.That(contract.PageCount).IsEqualTo(464);
    }

    [Test]
    public async Task ToContract_WhenMappingUser_ShouldCopyFieldsAndStringifyId()
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, Name = "Alice" };

        var contract = _mapper.ToContract(user);

        await Assert.That(contract.Id).IsEqualTo(id.ToString());
        await Assert.That(contract.Name).IsEqualTo("Alice");
    }
}
