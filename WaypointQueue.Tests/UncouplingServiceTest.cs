using Model;
using Moq;
using WaypointQueue.Services;
using WaypointQueue.Wrappers;

namespace WaypointQueue.Tests
{
    public class UncouplingServiceTest
    {
        private readonly Mock<ICarService> _carServiceMock;
        private readonly Mock<IOpsControllerWrapper> _opsControllerWrapperMock;

        private readonly UncouplingService _sut;

        public UncouplingServiceTest()
        {
            _carServiceMock = new Mock<ICarService>();
            _opsControllerWrapperMock = new Mock<IOpsControllerWrapper>();
            _sut = new UncouplingService(_carServiceMock.Object, _opsControllerWrapperMock.Object);
        }

        [Fact]
        public void FindCarToUncouple_FromEndA_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(0),
                GenerateMockCar(1),
                GenerateMockCar(2),
            ];

            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("2", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.B, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_FromEndB_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(7),
                GenerateMockCar(8),
                GenerateMockCar(9),
            ];

            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("7", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.A, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_FromEndB_OrderDoesNotMatter()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(9),
                GenerateMockCar(8),
                GenerateMockCar(7),
                GenerateMockCar(6),
            ];


            var (carToUncouple, endToUncouple) = _sut.FindCarToUncouple(carsToCut, consistFromEndA);

            Assert.Equal("6", carToUncouple.id);
            Assert.Equal(Car.LogicalEnd.A, endToUncouple);
        }

        [Fact]
        public void FindCarToUncouple_ThrowsException_WhenCarsAreNotSubsetOfConsist()
        {
            List<Car> consistFromEndA = GenerateConsist();
            List<Car> carsToCut =
            [
                GenerateMockCar(15),
                GenerateMockCar(16),
            ];

            Assert.Throws<InvalidOperationException>(() => _sut.FindCarToUncouple(carsToCut, consistFromEndA));
        }

        [Fact]
        public void CalculateCutForPickupByCount_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 4;
            int carsToPickup = 2;

            List<Car> carsToCut = _sut.CalculateCutForPickupByCount(consist, indexOfCoupledCar, carsToPickup);

            Assert.Equal(3, carsToCut.Count);
            Assert.Equal("0", carsToCut.First().id);
            Assert.Equal("2", carsToCut.Last().id);
        }

        [Fact]
        public void CalculateCutForPickupByCount_ClampsNumberOfCars()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 4;
            int carsToPickup = 150;

            List<Car> carsToCut = _sut.CalculateCutForPickupByCount(consist, indexOfCoupledCar, carsToPickup);

            Assert.Empty(carsToCut);
        }

        [Fact]
        public void CalculateCutForPickupByCount_OneCar()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 9;
            int carsToPickup = 1;

            List<Car> carsToCut = _sut.CalculateCutForPickupByCount(consist, indexOfCoupledCar, carsToPickup);

            Assert.Equal(9, carsToCut.Count);
            Assert.Equal("0", carsToCut.First().id);
            Assert.Equal("8", carsToCut.Last().id);
        }

        [Fact]
        public void CalculateCutForDropoffByCount_Success()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 4;
            int carsToDropoff = 2;

            List<Car> carsToCut = _sut.CalculateCutForDropoffByCount(consist, indexOfCoupledCar, carsToDropoff);

            Assert.Equal(7, carsToCut.Count);
            Assert.Equal("0", carsToCut.First().id);
            Assert.Equal("6", carsToCut.Last().id);
        }

        [Fact]
        public void CalculateCutForDropoffByCount_ClampsNumberOfCars()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 4;
            int carsToDropoff = 150;

            List<Car> carsToCut = _sut.CalculateCutForDropoffByCount(consist, indexOfCoupledCar, carsToDropoff);

            Assert.Equal(10, carsToCut.Count);
            Assert.Equal("0", carsToCut.First().id);
            Assert.Equal("9", carsToCut.Last().id);
        }

        [Fact]
        public void CalculateCutForDropoffByCount_OneCar()
        {
            // 0-1-2-3-4-5-6-7-8-9
            List<Car> consist = GenerateConsist();
            int indexOfCoupledCar = 6;
            int carsToDropoff = 1;

            List<Car> carsToCut = _sut.CalculateCutForDropoffByCount(consist, indexOfCoupledCar, carsToDropoff);

            Assert.Equal(8, carsToCut.Count);
            Assert.Equal("0", carsToCut.First().id);
            Assert.Equal("7", carsToCut.Last().id);
        }

        private List<Car> GenerateConsist()
        {
            List<Car> cars =
            [
                GenerateMockCar(0),
                GenerateMockCar(1),
                GenerateMockCar(2),
                GenerateMockCar(3),
                GenerateMockCar(4),
                GenerateMockCar(5),
                GenerateMockCar(6),
                GenerateMockCar(7),
                GenerateMockCar(8),
                GenerateMockCar(9),
            ];
            return cars;
        }

        private Car GenerateMockCar(int roadNumber)
        {
            var mockCar = new Mock<Car>();
            mockCar.Object.SetIdent(new CarIdent("PRR", roadNumber.ToString()));
            mockCar.Object.id = roadNumber.ToString();
            return mockCar.Object;
        }
    }
}
