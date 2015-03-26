using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Discounts;
using Nop.Core.Domain.Orders;
using Nop.Services.Catalog.Cache;
using Nop.Services.Discounts;

namespace Nop.Services.Catalog
{
    /// <summary>
    /// Price calculation service
    /// </summary>
    public partial class PriceCalculationService : IPriceCalculationService
    {
        #region Fields

        private readonly IWorkContext _workContext;
        private readonly IStoreContext _storeContext;
        private readonly IDiscountService _discountService;
        private readonly ICategoryService _categoryService;
        private readonly IProductAttributeParser _productAttributeParser;
        private readonly IProductService _productService;
        private readonly ICacheManager _cacheManager;
        private readonly ShoppingCartSettings _shoppingCartSettings;
        private readonly CatalogSettings _catalogSettings;

        #endregion

        #region Ctor

        public PriceCalculationService(IWorkContext workContext,
            IStoreContext storeContext,
            IDiscountService discountService, 
            ICategoryService categoryService,
            IProductAttributeParser productAttributeParser, 
            IProductService productService,
            ICacheManager cacheManager,
            ShoppingCartSettings shoppingCartSettings, 
            CatalogSettings catalogSettings)
        {
            this._workContext = workContext;
            this._storeContext = storeContext;
            this._discountService = discountService;
            this._categoryService = categoryService;
            this._productAttributeParser = productAttributeParser;
            this._productService = productService;
            this._cacheManager = cacheManager;
            this._shoppingCartSettings = shoppingCartSettings;
            this._catalogSettings = catalogSettings;
        }
        
        #endregion

        #region Nested classes

        [Serializable]
        protected class ProductPriceForCaching
        {
            public decimal Price { get; set; }
            public decimal AppliedDiscountAmount { get; set; }
            public int AppliedDiscountId { get; set; }
        }
        #endregion

        #region Utilities

        /// <summary>
        /// Gets allowed discounts
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">Customer</param>
        /// <returns>Discounts</returns>
        protected virtual IList<Discount> GetAllowedDiscounts(Product product, 
            Customer customer)
        {
            var allowedDiscounts = new List<Discount>();
            if (_catalogSettings.IgnoreDiscounts)
                return allowedDiscounts;

            if (product.HasDiscountsApplied)
            {
                //we use this property ("HasDiscountsApplied") for performance optimziation to avoid unnecessary database calls
                foreach (var discount in product.AppliedDiscounts)
                {
                    if (_discountService.IsDiscountValid(discount, customer) &&
                        discount.DiscountType == DiscountType.AssignedToSkus &&
                        !allowedDiscounts.ContainsDiscount(discount))
                        allowedDiscounts.Add(discount);
                }
            }

            //performance optimization
            //load all category discounts just to ensure that we have at least one
            if (_discountService.GetAllDiscounts(DiscountType.AssignedToCategories).Any())
            {
                var productCategories = _categoryService.GetProductCategoriesByProductId(product.Id);
                if (productCategories != null)
                {
                    foreach (var productCategory in productCategories)
                    {
                        var category = productCategory.Category;

                        if (category.HasDiscountsApplied)
                        {
                            //we use this property ("HasDiscountsApplied") for performance optimziation to avoid unnecessary database calls
                            var categoryDiscounts = category.AppliedDiscounts;
                            foreach (var discount in categoryDiscounts)
                            {
                                if (_discountService.IsDiscountValid(discount, customer) &&
                                    discount.DiscountType == DiscountType.AssignedToCategories &&
                                    !allowedDiscounts.ContainsDiscount(discount))
                                    allowedDiscounts.Add(discount);
                            }
                        }
                    }
                }
            }
            return allowedDiscounts;
        }
        
        /// <summary>
        /// Gets a tier price
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">Customer</param>
        /// <param name="quantity">Quantity</param>
        /// <returns>Price</returns>
        protected virtual decimal? GetMinimumTierPrice(Product product, Customer customer, int quantity)
        {
            if (!product.HasTierPrices)
                return decimal.Zero;

            var tierPrices = product.TierPrices
                .OrderBy(tp => tp.Quantity)
                .ToList()
                .FilterByStore(_storeContext.CurrentStore.Id)
                .FilterForCustomer(customer)
                .RemoveDuplicatedQuantities();

            int previousQty = 1;
            decimal? previousPrice = null;
            foreach (var tierPrice in tierPrices)
            {
                //check quantity
                if (quantity < tierPrice.Quantity)
                    continue;
                if (tierPrice.Quantity < previousQty)
                    continue;

                //save new price
                previousPrice = tierPrice.Price;
                previousQty = tierPrice.Quantity;
            }
            
            return previousPrice;
        }

        /// <summary>
        /// Get number of rental periods (price ratio)
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <returns>Number of rental periods</returns>
        protected virtual int GetRentalPeriods(Product product, DateTime startDate, DateTime endDate)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            if (!product.IsRental)
                return 1;

            if (startDate.CompareTo(endDate) > 0)
                return 1;

            var totalDaysToRent = (endDate - startDate).TotalDays;
            if (totalDaysToRent <= 0)
                totalDaysToRent = 1;

            int configuredPeriodDays;
            switch (product.RentalPricePeriod)
            {
                case RentalPricePeriod.Days:
                    configuredPeriodDays = 1 * product.RentalPriceLength;
                    break;
                case RentalPricePeriod.Weeks:
                    configuredPeriodDays = 7 * product.RentalPriceLength;
                    break;
                case RentalPricePeriod.Months:
                    configuredPeriodDays = 30 * product.RentalPriceLength;
                    break;
                case RentalPricePeriod.Years:
                    configuredPeriodDays = 365 * product.RentalPriceLength;
                    break;
                default:
                    throw new NopException("Not supported rental period");
            }

            var totalPeriods = Convert.ToInt32(Math.Ceiling(totalDaysToRent/configuredPeriodDays));
            return totalPeriods;
        }

        /// <summary>
        /// Gets discount amount
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">The customer</param>
        /// <param name="productPriceWithoutDiscount">Already calculated product price without discount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Discount amount</returns>
        protected virtual decimal GetDiscountAmount(Product product,
            Customer customer,
            decimal productPriceWithoutDiscount,
            out Discount appliedDiscount)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            appliedDiscount = null;
            decimal appliedDiscountAmount = decimal.Zero;

            //we don't apply discounts to products with price entered by a customer
            if (product.CustomerEntersPrice)
                return appliedDiscountAmount;

            //discounts are disabled
            if (_catalogSettings.IgnoreDiscounts)
                return appliedDiscountAmount;

            var allowedDiscounts = GetAllowedDiscounts(product, customer);

            //no discounts
            if (allowedDiscounts.Count == 0)
                return appliedDiscountAmount;

            appliedDiscount = allowedDiscounts.GetPreferredDiscount(productPriceWithoutDiscount);

            if (appliedDiscount != null)
                appliedDiscountAmount = appliedDiscount.GetDiscountAmount(productPriceWithoutDiscount);

            return appliedDiscountAmount;
        }


        #endregion

        #region Methods




        /// <summary>
        /// Gets the final price
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">The customer</param>
        /// <param name="additionalCharge">Additional charge</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for final price computation</param>
        /// <param name="quantity">Shopping cart item quantity</param>
        /// <returns>Final price</returns>
        public virtual decimal GetFinalPrice(Product product,
            Customer customer,
            decimal additionalCharge = decimal.Zero,
            bool includeDiscounts = true,
            int quantity = 1)
        {
            decimal discountAmount;
            Discount appliedDiscount;
            return GetFinalPrice(product, customer, additionalCharge, includeDiscounts,
                quantity, out discountAmount, out appliedDiscount);
        }
        /// <summary>
        /// Gets the final price
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">The customer</param>
        /// <param name="additionalCharge">Additional charge</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for final price computation</param>
        /// <param name="quantity">Shopping cart item quantity</param>
        /// <param name="discountAmount">Applied discount amount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Final price</returns>
        public virtual decimal GetFinalPrice(Product product, 
            Customer customer,
            decimal additionalCharge, 
            bool includeDiscounts,
            int quantity,
            out decimal discountAmount,
            out Discount appliedDiscount)
        {
            return GetFinalPrice(product, customer,
                additionalCharge, includeDiscounts, quantity,
                null, null,
                out discountAmount, out appliedDiscount);
        }
        /// <summary>
        /// Gets the final price
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">The customer</param>
        /// <param name="additionalCharge">Additional charge</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for final price computation</param>
        /// <param name="quantity">Shopping cart item quantity</param>
        /// <param name="rentalStartDate">Rental period start date (for rental products)</param>
        /// <param name="rentalEndDate">Rental period end date (for rental products)</param>
        /// <param name="discountAmount">Applied discount amount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Final price</returns>
        public virtual decimal GetFinalPrice(Product product, 
            Customer customer,
            decimal additionalCharge, 
            bool includeDiscounts,
            int quantity,
            DateTime? rentalStartDate,
            DateTime? rentalEndDate,
            out decimal discountAmount,
            out Discount appliedDiscount)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            discountAmount = decimal.Zero;
            appliedDiscount = null;

            var cacheKey = string.Format(PriceCacheEventConsumer.PRODUCT_PRICE_MODEL_KEY,
                product.Id, 
                additionalCharge.ToString(CultureInfo.InvariantCulture),
                includeDiscounts, 
                quantity, 
                string.Join(",", customer.CustomerRoles.Where(cr => cr.Active).Select(cr => cr.Id).ToList()),
                _storeContext.CurrentStore.Id);
            var cacheTime = _catalogSettings.CacheProductPrices ? 60 : 0;
            //we do not cache price for rental products
            //otherwise, it can cause memory leaks (to store all possible date period combinations)
            if (product.IsRental)
                cacheTime = 0;
            var cachedPrice = _cacheManager.Get(cacheKey, cacheTime, () =>
            {
                var result = new ProductPriceForCaching();

                //initial price
                decimal price = product.Price;

                //special price
                var specialPrice = product.GetSpecialPrice();
                if (specialPrice.HasValue)
                    price = specialPrice.Value;

                //tier prices
                if (product.HasTierPrices)
                {
                    decimal? tierPrice = GetMinimumTierPrice(product, customer, quantity);
                    if (tierPrice.HasValue)
                        price = Math.Min(price, tierPrice.Value);
                }

                //additional charge
                price = price + additionalCharge;

                //rental products
                if (product.IsRental)
                    if (rentalStartDate.HasValue && rentalEndDate.HasValue)
                        price = price*GetRentalPeriods(product, rentalStartDate.Value, rentalEndDate.Value);

                if (includeDiscounts)
                {
                    //discount
                    Discount tmpAppliedDiscount;
                    decimal tmpDiscountAmount = GetDiscountAmount(product, customer, price, out tmpAppliedDiscount);
                    price = price - tmpDiscountAmount;

                    if (tmpAppliedDiscount != null)
                    {
                        result.AppliedDiscountId = tmpAppliedDiscount.Id;
                        result.AppliedDiscountAmount = tmpDiscountAmount;
                    }
                }

                if (price < decimal.Zero)
                    price = decimal.Zero;

                result.Price = price;
                return result;
            });

            if (includeDiscounts)
            {
                //Discount instance cannnot be cached between requests (when "catalogSettings.CacheProductPrices" is "true)
                //This is limitation of Entity Framework
                //That's why we load it here after working with cache
                appliedDiscount = _discountService.GetDiscountById(cachedPrice.AppliedDiscountId);
                if (appliedDiscount != null)
                {
                    discountAmount = cachedPrice.AppliedDiscountAmount;
                }
            }

            return cachedPrice.Price;
        }



        /// <summary>
        /// Gets the shopping cart unit price (one item)
        /// </summary>
        /// <param name="shoppingCartItem">The shopping cart item</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for price computation</param>
        /// <returns>Shopping cart unit price (one item)</returns>
        public virtual decimal GetUnitPrice(ShoppingCartItem shoppingCartItem,
            bool includeDiscounts = true)
        {
            decimal discountAmount;
            Discount appliedDiscount;
            return GetUnitPrice(shoppingCartItem, includeDiscounts,
                out discountAmount, out appliedDiscount);
        }
        /// <summary>
        /// Gets the shopping cart unit price (one item)
        /// </summary>
        /// <param name="shoppingCartItem">The shopping cart item</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for price computation</param>
        /// <param name="discountAmount">Applied discount amount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Shopping cart unit price (one item)</returns>
        public virtual decimal GetUnitPrice(ShoppingCartItem shoppingCartItem,
            bool includeDiscounts,
            out decimal discountAmount,
            out Discount appliedDiscount)
        {
            if (shoppingCartItem == null)
                throw new ArgumentNullException("shoppingCartItem");

            return GetUnitPrice(shoppingCartItem.Product,
                shoppingCartItem.Customer,
                shoppingCartItem.ShoppingCartType,
                shoppingCartItem.Quantity,
                shoppingCartItem.AttributesXml,
                shoppingCartItem.CustomerEnteredPrice,
                shoppingCartItem.RentalStartDateUtc,
                shoppingCartItem.RentalEndDateUtc,
                includeDiscounts,
                out discountAmount,
                out appliedDiscount);
        }
        /// <summary>
        /// Gets the shopping cart unit price (one item)
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="customer">Customer</param>
        /// <param name="shoppingCartType">Shopping cart type</param>
        /// <param name="quantity">Quantity</param>
        /// <param name="attributesXml">Product atrributes (XML format)</param>
        /// <param name="customerEnteredPrice">Customer entered price (if specified)</param>
        /// <param name="rentalStartDate">Rental start date (null for not rental products)</param>
        /// <param name="rentalEndDate">Rental end date (null for not rental products)</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for price computation</param>
        /// <param name="discountAmount">Applied discount amount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Shopping cart unit price (one item)</returns>
        public virtual decimal GetUnitPrice(Product product,
            Customer customer, 
            ShoppingCartType shoppingCartType,
            int quantity,
            string attributesXml,
            decimal customerEnteredPrice,
            DateTime? rentalStartDate, DateTime? rentalEndDate,
            bool includeDiscounts,
            out decimal discountAmount,
            out Discount appliedDiscount)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            if (customer == null)
                throw new ArgumentNullException("customer");

            discountAmount = decimal.Zero;
            appliedDiscount = null;

            decimal finalPrice;

            var combination = _productAttributeParser.FindProductAttributeCombination(product, attributesXml);
            if (combination != null && combination.OverriddenPrice.HasValue)
            {
                finalPrice = combination.OverriddenPrice.Value;
            }
            else
            {
                //summarize price of all attributes
                decimal attributesTotalPrice = decimal.Zero;
                var attributeValues = _productAttributeParser.ParseProductAttributeValues(attributesXml);
                if (attributeValues != null)
                {
                    foreach (var attributeValue in attributeValues)
                    {
                        attributesTotalPrice += GetProductAttributeValuePriceAdjustment(attributeValue);
                    }
                }

                //get price of a product (with previously calculated price of all attributes)
                if (product.CustomerEntersPrice)
                {
                    finalPrice = customerEnteredPrice;
                }
                else
                {
                    int qty;
                    if (_shoppingCartSettings.GroupTierPricesForDistinctShoppingCartItems)
                    {
                        //the same products with distinct product attributes could be stored as distinct "ShoppingCartItem" records
                        //so let's find how many of the current products are in the cart
                        qty = customer.ShoppingCartItems
                            .Where(x => x.ProductId == product.Id)
                            .Where(x => x.ShoppingCartType == shoppingCartType)
                            .Sum(x => x.Quantity);
                        if (qty == 0)
                        {
                            qty = quantity;
                        }
                    }
                    else
                    {
                        qty = quantity;
                    }
                    finalPrice = GetFinalPrice(product,
                        customer,
                        attributesTotalPrice,
                        includeDiscounts,
                        qty,
                        product.IsRental ? rentalStartDate : null,
                        product.IsRental ? rentalEndDate : null,
                        out discountAmount, out appliedDiscount);
                }
            }
            
            //rounding
            if (_shoppingCartSettings.RoundPricesDuringCalculation)
                finalPrice = RoundingHelper.RoundPrice(finalPrice);

            return finalPrice;
        }
        /// <summary>
        /// Gets the shopping cart item sub total
        /// </summary>
        /// <param name="shoppingCartItem">The shopping cart item</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for price computation</param>
        /// <returns>Shopping cart item sub total</returns>
        public virtual decimal GetSubTotal(ShoppingCartItem shoppingCartItem,
            bool includeDiscounts = true)
        {
            decimal discountAmount;
            Discount appliedDiscount;
            return GetSubTotal(shoppingCartItem, includeDiscounts, out discountAmount, out appliedDiscount);
        }
        /// <summary>
        /// Gets the shopping cart item sub total
        /// </summary>
        /// <param name="shoppingCartItem">The shopping cart item</param>
        /// <param name="includeDiscounts">A value indicating whether include discounts or not for price computation</param>
        /// <param name="discountAmount">Applied discount amount</param>
        /// <param name="appliedDiscount">Applied discount</param>
        /// <returns>Shopping cart item sub total</returns>
        public virtual decimal GetSubTotal(ShoppingCartItem shoppingCartItem,
            bool includeDiscounts,
            out decimal discountAmount,
            out Discount appliedDiscount)
        {
            if (shoppingCartItem == null)
                throw new ArgumentNullException("shoppingCartItem");

            decimal subTotal;

            //unit price
            var unitPrice = GetUnitPrice(shoppingCartItem, includeDiscounts,
                out discountAmount, out appliedDiscount);

            //discount
            if (appliedDiscount != null)
            {
                if (appliedDiscount.MaximumDiscountedQuantity.HasValue &&
                    shoppingCartItem.Quantity > appliedDiscount.MaximumDiscountedQuantity.Value)
                {
                    //we cannot apply discount for all shopping cart items
                    var discountedQuantity = appliedDiscount.MaximumDiscountedQuantity.Value;
                    var discountedSubTotal = unitPrice * discountedQuantity;
                    discountAmount = discountAmount * discountedQuantity;
                    
                    var notDiscountedQuantity = shoppingCartItem.Quantity - appliedDiscount.MaximumDiscountedQuantity.Value;
                    var notDiscountedUnitPrice = GetUnitPrice(shoppingCartItem, false);
                    var notDiscountedSubTotal = notDiscountedUnitPrice*notDiscountedQuantity;

                    subTotal = discountedSubTotal + notDiscountedSubTotal;
                }
                else
                {
                    //discount is applied to all items (quantity)
                    //calculate discount amount for all items
                    discountAmount = discountAmount * shoppingCartItem.Quantity;

                    subTotal = unitPrice * shoppingCartItem.Quantity;
                }
            }
            else
            {
                subTotal = unitPrice * shoppingCartItem.Quantity;
            }
            return subTotal;
        }
        


        /// <summary>
        /// Gets the product cost (one item)
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="attributesXml">Shopping cart item attributes in XML</param>
        /// <returns>Product cost (one item)</returns>
        public virtual decimal GetProductCost(Product product, string attributesXml)
        {
            if (product == null)
                throw new ArgumentNullException("product");

            decimal cost = product.ProductCost;
            var attributeValues = _productAttributeParser.ParseProductAttributeValues(attributesXml);
            foreach (var attributeValue in attributeValues)
            {
                switch (attributeValue.AttributeValueType)
                {
                    case AttributeValueType.Simple:
                        {
                            //simple attribute
                            cost += attributeValue.Cost;
                        }
                        break;
                    case AttributeValueType.AssociatedToProduct:
                        {
                            //bundled product
                            var associatedProduct = _productService.GetProductById(attributeValue.AssociatedProductId);
                            if (associatedProduct != null)
                                cost += associatedProduct.ProductCost * attributeValue.Quantity;
                        }
                        break;
                    default:
                        break;
                }
            }

            return cost;
        }



        /// <summary>
        /// Get a price adjustment of a product attribute value
        /// </summary>
        /// <param name="value">Product attribute value</param>
        /// <returns>Price adjustment</returns>
        public virtual decimal GetProductAttributeValuePriceAdjustment(ProductAttributeValue value)
        {
            if (value == null)
                throw new ArgumentNullException("value");

            var adjustment = decimal.Zero;
            switch (value.AttributeValueType)
            {
                case AttributeValueType.Simple:
                    {
                        //simple attribute
                        adjustment = value.PriceAdjustment;
                    }
                    break;
                case AttributeValueType.AssociatedToProduct:
                    {
                        //bundled product
                        var associatedProduct = _productService.GetProductById(value.AssociatedProductId);
                        if (associatedProduct != null)
                        {
                            adjustment = GetFinalPrice(associatedProduct, _workContext.CurrentCustomer, includeDiscounts: true) * value.Quantity;
                        }
                    }
                    break;
                default:
                    break;
            }

            return adjustment;
        }

        #endregion
    }
}
