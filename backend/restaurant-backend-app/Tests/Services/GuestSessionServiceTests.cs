using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    // ---------- DeleteGuestSessionAsync ----------

    private static Dish CreateDish(string id, string name, decimal price) => new()
    {
        Id = id,
        Name = name,
        Price = price,
        State = DishState.Available
    };

    private static GuestSession CreateSession(string id, string groupReservationId, string name, string? dishId = null, int quantity = 0) => new()
    {
        Id = id,
        GroupReservationId = groupReservationId,
        GuestName = name,
        Dishes = dishId is null ? [] : [new OrderItem { DishId = dishId, Quantity = quantity }]
    };

    private void SetupReservationOwner(string ownerUserId, GroupReservation group, Reservation reservation)
    {
        group.ReservationId = reservation.Id;
        reservation.CustomerId = ownerUserId;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_OwnerDeletesGuest_DeletesSessionAndReturnsUpdatedSnapshot(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);
        removed.IsSubmitted = true;
        GuestSession remaining = CreateSession("session2", group.GroupReservationId, "Olena", "dish2", 1);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(removed.Id))
            .ReturnsAsync(removed);

        // Simulates an eventually consistent GSI: the group query still returns the session being removed.
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed, remaining });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new[] { CreateDish("dish1", "Caesar Salad", 180), CreateDish("dish2", "Tom Yum", 220) });

        GetAllSessionsResponse result = await _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id);

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(removed), Times.Once);

        Participant participant = Assert.Single(result.Participants);
        Assert.Equal(remaining.Id, participant.Id);
        Assert.Equal(ParticipantType.Guest, participant.Type);
        Assert.Equal("Olena", participant.Name);
        Assert.False(participant.IsSubmitted);
        Assert.Equal(220m, participant.Total);

        var item = Assert.Single(participant.Items);
        Assert.Equal("dish2", item.Dish.Id);
        Assert.Equal(1, item.Quantity);

        Assert.Equal(220m, result.Total);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_OwnerDeletesGuest_OnlyLooksUpDishesOfRemainingSessions(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);
        GuestSession remaining = CreateSession("session2", group.GroupReservationId, "Olena", "dish2", 1);

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(removed.Id)).ReturnsAsync(removed);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed, remaining });
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new[] { CreateDish("dish2", "Tom Yum", 220) });

        await _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id);

        _mockDishRepository.Verify(
            r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "dish2" }))),
            Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_LastGuestRemoved_ReturnsEmptySnapshot(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(removed.Id)).ReturnsAsync(removed);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed });
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new List<Dish>());

        GetAllSessionsResponse result = await _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id);

        Assert.Empty(result.Participants);
        Assert.Equal(0m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(removed), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_GroupContainsOwnerSession_ReturnsOwnerParticipantFirst(
        string ownerUserId, string ownerName, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);
        GuestSession guest = CreateSession("session2", group.GroupReservationId, "Olena", "dish2", 1);
        GuestSession owner = CreateSession("owner-session", group.GroupReservationId, "Owner", "dish1", 1);
        owner.IsOwnerSession = true;
        owner.OwnerUserId = ownerUserId;
        owner.IsSubmitted = true;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(removed.Id)).ReturnsAsync(removed);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed, guest, owner });
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new[] { CreateDish("dish1", "Caesar Salad", 180), CreateDish("dish2", "Tom Yum", 220) });

        var user = new UserType { Attributes = [new AttributeType { Name = "given_name", Value = ownerName }] };
        _mockUserRepository.Setup(r => r.GetUserByIdAsync(ownerUserId)).ReturnsAsync(user);

        GetAllSessionsResponse result = await _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id);

        Assert.Equal(2, result.Participants.Count());

        Participant first = result.Participants.First();
        Assert.Equal(owner.Id, first.Id);
        Assert.Equal(ParticipantType.Owner, first.Type);
        Assert.Equal(ownerName, first.Name);
        Assert.True(first.IsSubmitted);
        Assert.Equal(180m, first.Total);

        Participant second = result.Participants.Last();
        Assert.Equal(guest.Id, second.Id);
        Assert.Equal(ParticipantType.Guest, second.Type);

        Assert.Equal(400m, result.Total);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_RemainingSessionHasDishThatNoLongerExists_SkipsItInsteadOfFailing(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);
        GuestSession remaining = CreateSession("session2", group.GroupReservationId, "Olena", "dish2", 1);
        remaining.Dishes.Add(new OrderItem { DishId = "deleted-dish", Quantity = 3 });

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(removed.Id)).ReturnsAsync(removed);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed, remaining });
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new[] { CreateDish("dish2", "Tom Yum", 220) });

        GetAllSessionsResponse result = await _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id);

        Participant participant = Assert.Single(result.Participants);
        Assert.Single(participant.Items);
        Assert.Equal(220m, participant.Total);
        Assert.Equal(220m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(removed), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_BuildingSnapshotFails_DoesNotDeleteSession(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession removed = CreateSession("session1", group.GroupReservationId, "Andrii", "dish1", 2);
        GuestSession remaining = CreateSession("session2", group.GroupReservationId, "Olena", "dish2", 1);

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(removed.Id)).ReturnsAsync(removed);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync(new List<GuestSession> { removed, remaining });
        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ThrowsAsync(new InvalidOperationException("dish table unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, removed.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_GroupReservationNotFound_ThrowsGroupReservationNotFoundException(
        string userId, string groupReservationId, string guestSessionId)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(userId, groupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_ParentReservationNotFound_ThrowsReservationNotFoundException(
        string userId, GroupReservation group, string guestSessionId)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(userId, group.GroupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_CallerIsNotReservationOwner_ThrowsActionForbiddenException(
        string ownerUserId, string otherUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession guest = CreateSession("session1", group.GroupReservationId, "Andrii");
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guest.Id)).ReturnsAsync(guest);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(otherUserId, group.GroupReservationId, guest.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_GuestSessionNotFound_ThrowsGuestSessionNotFoundException(
        string ownerUserId, string guestSessionId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_GuestSessionBelongsToAnotherGroup_ThrowsValidationException(
        string ownerUserId, string anotherGroupReservationId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession foreign = CreateSession("foreign-session", anotherGroupReservationId, "Andrii");
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(foreign.Id)).ReturnsAsync(foreign);

        // Exact type on purpose: "not found" (404) derives from ValidationException, "wrong group" must be a plain one (400).
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, foreign.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task DeleteGuestSessionAsync_TargetIsOwnerSession_ThrowsValidationExceptionAndKeepsSession(
        string ownerUserId, GroupReservation group, Reservation reservation)
    {
        SetupReservationOwner(ownerUserId, group, reservation);

        GuestSession owner = CreateSession("owner-session", group.GroupReservationId, "Owner");
        owner.IsOwnerSession = true;
        owner.OwnerUserId = ownerUserId;
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(owner.Id)).ReturnsAsync(owner);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(ownerUserId, group.GroupReservationId, owner.Id));

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }
}





