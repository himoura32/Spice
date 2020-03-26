using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Spice.Data;
using Spice.Models;
using Spice.Models.ViewModels;
using Spice.Utility;
using Stripe;

namespace Spice.Areas.Customer.Controllers
{
    [Area("Customer")]
    public class CartController : Controller
    {
        private readonly ApplicationDbContext _db;

        [BindProperty]
        public OrderDetailsCart DetailCart { get; set; }

        public CartController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            DetailCart = new OrderDetailsCart()
            {
                OrderHeader = new Models.OrderHeader()
            };

            DetailCart.OrderHeader.OrderTotal = 0;

            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            var cart = _db.ShoppingCart.Where(c => c.ApplicationUserId == claim.Value);

            if(cart != null)
            {
                DetailCart.ListCart = cart.ToList();
            }

            foreach (var list in DetailCart.ListCart)
            {
                list.MenuItem = await _db.MenuItem.FirstOrDefaultAsync(m => m.Id == list.MenuItemId);
                DetailCart.OrderHeader.OrderTotal = DetailCart.OrderHeader.OrderTotal + (list.MenuItem.Price * list.Count);
                list.MenuItem.Description = SD.ConvertToRawHtml(list.MenuItem.Description);
                
                if (list.MenuItem.Description.Length > 100)
                {
                    list.MenuItem.Description = list.MenuItem.Description.Substring(0, 99) + "...";
                }
            }
            DetailCart.OrderHeader.OrderTotalOriginal = DetailCart.OrderHeader.OrderTotal;

            if(HttpContext.Session.GetString(SD.ssCouponCode) != null)
            {
                DetailCart.OrderHeader.CouponCode = HttpContext.Session.GetString(SD.ssCouponCode);
                var couponFromDb = await _db.Coupon.Where(c => c.Name.ToLower() == DetailCart.OrderHeader.CouponCode.ToLower()).FirstOrDefaultAsync();
                DetailCart.OrderHeader.OrderTotal = SD.DiscountedPrice(couponFromDb, DetailCart.OrderHeader.OrderTotalOriginal);
            }

            return View(DetailCart);
        }

        public async Task<IActionResult> Summary()
        {
            DetailCart = new OrderDetailsCart()
            {
                OrderHeader = new Models.OrderHeader()
            };

            DetailCart.OrderHeader.OrderTotal = 0;

            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
            ApplicationUser applicationUser = await _db.ApplicationUser.Where(c => c.Id == claim.Value).FirstOrDefaultAsync();

            var cart = _db.ShoppingCart.Where(c => c.ApplicationUserId == claim.Value);

            if (cart != null)
            {
                DetailCart.ListCart = cart.ToList();
            }

            foreach (var list in DetailCart.ListCart)
            {
                list.MenuItem = await _db.MenuItem.FirstOrDefaultAsync(m => m.Id == list.MenuItemId);
                DetailCart.OrderHeader.OrderTotal = DetailCart.OrderHeader.OrderTotal + (list.MenuItem.Price * list.Count);
                
            }
            DetailCart.OrderHeader.OrderTotalOriginal = DetailCart.OrderHeader.OrderTotal;
            DetailCart.OrderHeader.PickUpName = applicationUser.Name;
            DetailCart.OrderHeader.PhoneNumber = applicationUser.PhoneNumber;
            DetailCart.OrderHeader.PickUpTime = DateTime.Now;

            if (HttpContext.Session.GetString(SD.ssCouponCode) != null)
            {
                DetailCart.OrderHeader.CouponCode = HttpContext.Session.GetString(SD.ssCouponCode);
                var couponFromDb = await _db.Coupon.Where(c => c.Name.ToLower() == DetailCart.OrderHeader.CouponCode.ToLower()).FirstOrDefaultAsync();
                DetailCart.OrderHeader.OrderTotal = SD.DiscountedPrice(couponFromDb, DetailCart.OrderHeader.OrderTotalOriginal);
            }

            return View(DetailCart);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [ActionName("Summary")]
        public async Task<IActionResult> SummaryPost(string stripeToken)
        {
            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            DetailCart.ListCart = await _db.ShoppingCart.Where(c => c.ApplicationUserId == claim.Value).ToListAsync();

            DetailCart.OrderHeader.PaymentStatus = SD.PaymentStatusPending;
            DetailCart.OrderHeader.OrderDate = DateTime.Now;
            DetailCart.OrderHeader.UserId = claim.Value;
            DetailCart.OrderHeader.Status = SD.PaymentStatusPending;
            DetailCart.OrderHeader.PickUpTime = Convert.ToDateTime(DetailCart.OrderHeader.PickUpDate.ToShortDateString() + " " + DetailCart.OrderHeader.PickUpTime.ToShortTimeString());

            List<OrderDetails> orderDetailsList = new List<OrderDetails>();
            _db.OrderHeader.Add(DetailCart.OrderHeader);
            await _db.SaveChangesAsync();

            DetailCart.OrderHeader.OrderTotalOriginal = 0;            

            foreach (var item in DetailCart.ListCart)
            {
                item.MenuItem = await _db.MenuItem.FirstOrDefaultAsync(m => m.Id == item.MenuItemId);
                OrderDetails orderDetails = new OrderDetails
                {
                    MenuItemId = item.MenuItemId,
                    OrderId = DetailCart.OrderHeader.Id,
                    Description = item.MenuItem.Description,
                    Name = item.MenuItem.Name,
                    Price = item.MenuItem.Price,
                    Count = item.Count
                };
                DetailCart.OrderHeader.OrderTotalOriginal += orderDetails.Count * orderDetails.Price;
                _db.OrderDetails.Add(orderDetails);
            }            

            if (HttpContext.Session.GetString(SD.ssCouponCode) != null)
            {
                DetailCart.OrderHeader.CouponCode = HttpContext.Session.GetString(SD.ssCouponCode);
                var couponFromDb = await _db.Coupon.Where(c => c.Name.ToLower() == DetailCart.OrderHeader.CouponCode.ToLower()).FirstOrDefaultAsync();
                DetailCart.OrderHeader.OrderTotal = SD.DiscountedPrice(couponFromDb, DetailCart.OrderHeader.OrderTotalOriginal);
            }
            else
            {
                DetailCart.OrderHeader.OrderTotal = DetailCart.OrderHeader.OrderTotalOriginal;
            }
            DetailCart.OrderHeader.CouponCodeDiscount = DetailCart.OrderHeader.OrderTotalOriginal - DetailCart.OrderHeader.OrderTotal;

            _db.ShoppingCart.RemoveRange(DetailCart.ListCart);
            HttpContext.Session.SetInt32(SD.ssShoppingCartCount, 0);
            await _db.SaveChangesAsync();

            var options = new ChargeCreateOptions
            {
                Amount = Convert.ToInt32(DetailCart.OrderHeader.OrderTotal * 100),
                Currency = "usd",
                Description = "Order ID: " + DetailCart.OrderHeader.Id,
                Source = stripeToken
            };
            var service = new ChargeService();
            Charge charge = service.Create(options);

            if (charge.BalanceTransactionId == null)
            {
                DetailCart.OrderHeader.PaymentStatus = SD.PaymentStatusRejected;
            }
            else
            {
                DetailCart.OrderHeader.TransactionId = charge.BalanceTransactionId;
            }

            if (charge.Status.ToLower() == "succeeded")
            {
                DetailCart.OrderHeader.PaymentStatus = SD.PaymentStatusApproved;
                DetailCart.OrderHeader.Status = SD.StatusSubmitted;
            }
            else
            {
                DetailCart.OrderHeader.PaymentStatus = SD.PaymentStatusRejected;
            }

            await _db.SaveChangesAsync();
            //return RedirectToAction("Index", "Home");
            return RedirectToAction("Confirm", "Order", new { id = DetailCart.OrderHeader.Id });
        }

        public IActionResult AddCoupon()
        {
            if(DetailCart.OrderHeader.CouponCode == null)
            {
                DetailCart.OrderHeader.CouponCode = "";
            }
            HttpContext.Session.SetString(SD.ssCouponCode, DetailCart.OrderHeader.CouponCode);

            return RedirectToAction(nameof(Index));
        }

        public IActionResult RemoveCoupon()
        {            
            HttpContext.Session.SetString(SD.ssCouponCode, string.Empty);

            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Plus(int cartId)
        {
            var cart = await _db.ShoppingCart.FirstOrDefaultAsync(c => c.Id == cartId);
            cart.Count += 1;
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Minus(int cartId)
        {
            var cart = await _db.ShoppingCart.FirstOrDefaultAsync(c => c.Id == cartId);
            if(cart.Count == 1)
            {
                _db.ShoppingCart.Remove(cart);
                await _db.SaveChangesAsync();

                var cnt = _db.ShoppingCart.Where(u => u.ApplicationUserId == cart.ApplicationUserId).ToList().Count;
                HttpContext.Session.SetInt32(SD.ssShoppingCartCount, cnt);
            }
            else
            {
                cart.Count -= 1;
                await _db.SaveChangesAsync();
            }            
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Remove(int cartId)
        {
            var cart = await _db.ShoppingCart.FirstOrDefaultAsync(c => c.Id == cartId);

            _db.ShoppingCart.Remove(cart);
            await _db.SaveChangesAsync();

            var cnt = _db.ShoppingCart.Where(u => u.ApplicationUserId == cart.ApplicationUserId).ToList().Count;
            HttpContext.Session.SetInt32(SD.ssShoppingCartCount, cnt);
            
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
    }
}