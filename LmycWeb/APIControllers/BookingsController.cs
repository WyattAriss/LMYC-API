﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LmycWeb.Data;
using LmycWeb.Models;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Authorization;
using AspNet.Security.OAuth.Validation;

namespace LmycWeb.APIControllers
{
    [Produces("application/json")]
    [Route("api/bookings")]
    [Authorize(AuthenticationSchemes = OAuthValidationDefaults.AuthenticationScheme)]
    [EnableCors("CorsPolicy")]
    public class BookingsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BookingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/Bookings
        [HttpGet]
        public IEnumerable<Booking> GetBookings()
        {
            //return _context.Bookings;
            return _context.Bookings.Include(b => b.Members).Include(b => b.NonMembers);
        }

        // GET: api/Bookings/5
        [HttpGet("{id}")]
        public async Task<IActionResult> GetBooking([FromRoute] string id)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var booking = await _context.Bookings.SingleOrDefaultAsync(m => m.BookingId == id);

            if (booking == null)
            {
                return NotFound();
            }

            return Ok(booking);
        }

        // GET: api/Bookings/[BoatId]/[SelectedDate]
        [HttpGet("{boatId}/{selectedDate}")]
        public async Task<IActionResult> GetAvailableStartTimes([FromRoute] string boatId, [FromRoute] DateTime selectedDate)
        {
            var boat = await _context.Boats.SingleOrDefaultAsync(b => b.BoatId == boatId);

            if (boat == null)
            {
                return BadRequest("Boat does not exist given ID!");
            }

            DateTime endTime = selectedDate;
            endTime = endTime.AddHours(23).AddMinutes(59).AddSeconds(59).AddMilliseconds(999);

            List<DateTime> startList = await _context.Bookings.Where(d => d.StartDateTime >= selectedDate
                && d.StartDateTime <= endTime && d.BoatId == boatId).Select(s => s.StartDateTime).ToListAsync();

            List<DateTime> endList = await _context.Bookings.Where(d => d.EndDateTime >= selectedDate
                && d.EndDateTime <= endTime && d.BoatId == boatId).Select(s => s.EndDateTime).ToListAsync();

            List<DateTime> availableTimeList = CreateSemiHourlyList(selectedDate);

            for (int i = 0, j = 1; i < startList.Count(); i++, j++)
            {
                // removes the available time if it exists in the available time list
                if (availableTimeList.IndexOf(startList[i]) != -1)
                {
                    availableTimeList.Remove(startList[i]);
                }

                TimeSpan betweenDiff = endList[i].Subtract(startList[i]);
                int amountOfHours = (int)betweenDiff.TotalHours - 1;
                DateTime hourTime = startList[i];

                // removes the hours the are booked
                for (int x = 0; x < amountOfHours; x++)
                {
                    hourTime = hourTime.AddHours(1);
                    availableTimeList.Remove(hourTime);
                }

                // removes the times that can not make up a full hour booking
                if (j < startList.Count())
                {
                    TimeSpan diff = startList[j].Subtract(endList[i]);
                    if (diff.TotalHours < 1)
                    {
                        availableTimeList.Remove(endList[i]);
                    }
                }

            }
            return Ok(availableTimeList);
        }

        // GET: api/Bookings/[BoatId]/[SelectedDate]/[StartTime]
        [HttpGet("{boatId}/{startTime}/{selectedDate}")]
        public async Task<IActionResult> GetAvailableEndTimes([FromRoute] string boatId, [FromRoute] DateTime startTime,
            [FromRoute] DateTime selectedDate)
        {
            var boat = await _context.Boats.SingleOrDefaultAsync(b => b.BoatId == boatId);

            if (boat == null)
            {
                return BadRequest("Boat does not exist given ID!");
            }
            else if (!IsValidDateRange(startTime, selectedDate))
            {
                return BadRequest("Start date cannot be after end date!");
            }
            else if (!IsValidTimeSpan(startTime, selectedDate))
            {
                return BadRequest("Bookings cannot be more than 3 days");
            }

            DateTime maxDate = selectedDate.AddDays(3);

            DateTime nextStartDate = await _context.Bookings.Where(d => d.StartDateTime > selectedDate
                && d.BoatId == boatId && d.StartDateTime < maxDate)
                .Select(s => s.StartDateTime).FirstOrDefaultAsync();

            if (nextStartDate == null)
            {
                nextStartDate = selectedDate.AddDays(3);
            }

            List<DateTime> availableTimesList = CreateSemiHourlyListWithRange(startTime, nextStartDate);

            return Ok(availableTimesList);
        }


        // GET: api/Bookings/5
        [Route("boat/{id}")]
        [HttpGet]
        public IActionResult GetBookingByBoat([FromRoute] string id)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var booking = _context.Bookings.Where(m => m.BoatId == id).Where(m => m.StartDateTime > DateTime.Now);

            if (booking == null)
            {
                return NotFound();
            }

            return Ok(booking);
        }


        // GET: api/Bookings/5
        [Route("user/{userName}")]
        [HttpGet]
        public async Task<IActionResult> GetBookingsByUser([FromRoute] string userName)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var user = await _context.Users.SingleOrDefaultAsync(u => u.UserName.Equals(userName));

            if (user == null)
            {
                return BadRequest("User not found");
            }

            var bookings = await _context.Bookings.Where(m => m.UserId.Equals(user.Id)).ToListAsync();

            if (bookings == null)
            {
                return NotFound();
            }

            return Ok(bookings);
        }



        // PUT: api/Bookings/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutBooking([FromRoute] string id, [FromBody] Booking newBooking)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (id != newBooking.BookingId)
            {
                return BadRequest();
            }

            var booking = await _context.Bookings.Include(b => b.Members).Include(b => b.NonMembers).SingleOrDefaultAsync(m => m.BookingId.Equals(newBooking.BookingId));

            if (booking == null)
            {
                return BadRequest();
            }

            //Check if boat is operational
            bool boatIsOperational = await CheckBoatIsInGoodStatusAsync(newBooking.BoatId);

            if (!boatIsOperational)
            {
                return BadRequest("Selected boat is not operational");
            }

            //Check if the members have enough for the newly allocated credits
            if (newBooking.CreditsUsed != 0)
            {
                bool result = await CheckMembersHaveEnoughCreditsForEditAsync(booking.Members, id);

                if (!result)
                {
                    return BadRequest("A member does not have enough credits.");
                }
            }

            //Refund old charges then Charge the newly allocated credits to each user 
            //if there is any credits to be charged

            //var oldBooking = await _context.Bookings.SingleOrDefaultAsync(m => m.BookingId == id);
            RefundBookingMemberCredits(booking.Members);

            if (newBooking.CreditsUsed != 0)
            {
                ChargeBookingMemberCredits(newBooking.Members);
            }

            //List<Member> memberList = await _context.Members.Where(m => m.BookingId == booking.BookingId).ToListAsync();
            //List<NonMember> nonMemberList = await _context.NonMembers.Where(m => m.BookingId == booking.BookingId).ToListAsync();

            //if (booking.Members != null)
            //{
            //    //Drop the old members from the db
            //    bool dropMembersResult = await RemoveOldMembersAsync(booking.Members, booking.BookingId);

            //    if (!dropMembersResult)
            //    {
            //        return BadRequest("unable to drop old members.");
            //    }

            //}

            //if (booking.NonMembers != null)
            //{
            //    //Drop the old non members from the db
            //    bool dropNonMembersResult = await RemoveOldNonMembersAsync(booking.NonMembers, booking.BookingId);

            //    if (!dropNonMembersResult)
            //    {
            //        return BadRequest("unable to drop old non members.");
            //    }
            //}

            //Calculate the total credit cost using the start and end dates
            //booking.CreditsUsed = (int)CalculateCredits(newBooking.StartDateTime, newBooking.EndDateTime, newBooking.BoatId);
            //booking.BoatId = newBooking.BoatId;
            //booking.EndDateTime = newBooking.EndDateTime;
            //booking.StartDateTime = newBooking.StartDateTime;
            //booking.Itinerary = newBooking.Itinerary;
            //memberList = newBooking.Members;
            //nonMemberList = newBooking.NonMembers;
            //booking.Members = memberList;
            //booking.NonMembers = nonMemberList;

            //_context.UpdateRange(memberList);
            //_context.UpdateRange(nonMemberList);
            //_context.Entry(booking).State = EntityState.Modified;

            _context.Entry(booking).CurrentValues.SetValues(newBooking);
            await _context.SaveChangesAsync();
            foreach (var member in booking.Members)
            {
                var existingMember = booking.Members
                    .FirstOrDefault(r => r.BookingId == member.BookingId
                                      && r.UserId == member.UserId
                    );

                if (existingMember == null)
                {
                    booking.Members.Add(member);
                }
                else
                {
                    _context.Entry(existingMember).CurrentValues.SetValues(member);
                }
            }
            await _context.SaveChangesAsync();
            foreach (var nonmember in booking.NonMembers)
            {
                var existingNonMember = booking.Members
                    .FirstOrDefault(r => r.BookingId == nonmember.BookingId
                    );

                if (existingNonMember == null)
                {
                    booking.NonMembers.Add(nonmember);
                }
                else
                {
                    _context.Entry(existingNonMember).CurrentValues.SetValues(nonmember);
                }
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!BookingExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }

        // POST: api/Bookings
        [HttpPost]
        public async Task<IActionResult> PostBooking([FromBody] Booking booking)
        {
            /* Validate Model from request */
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserName == booking.UserId);
            var userId = user.Id;
            booking.UserId = userId;

            //Check the member status of the user creating the booking
            bool goodStandingResult = await FullMemberGoodStatusCheckAsync(booking.UserId);

            if (!goodStandingResult)
            {
                return BadRequest("The user can't create the booking because they are not in good standing.");
            }

            //Check if boat is operational
            bool boatIsOperational = await CheckBoatIsInGoodStatusAsync(booking.BoatId);

            //Calculate the total credit cost using the start and end dates
            booking.CreditsUsed = (int)CalculateCredits(booking.StartDateTime, booking.EndDateTime, booking.BoatId);



            if (!boatIsOperational)
            {
                return BadRequest("Selected boat is not operational");
            }
            //Check if the booking requires credits
            else if (booking.CreditsUsed != 0)
            {
                bool result = await CheckMembersHaveEnoughCreditsAsync(booking.Members);

                if (!result)
                {
                    return BadRequest("A member does not have enough credits");
                }
            }
            else if (!IsValidDateRange(booking.StartDateTime, booking.EndDateTime))
            {
                return BadRequest("Start date cannot be after end date!");
            }
            else if (!IsValidTimeSpan(booking.StartDateTime, booking.EndDateTime))
            {
                return BadRequest("Bookings cannot be more than 3 days");
            }
            else if (!IsValidBookingDateRange(booking.BoatId, booking.StartDateTime, booking.EndDateTime).Result)
            {
                return BadRequest("Date has been previously reserved");
            }

            //Check skipper status of members
            int totalDays = (booking.EndDateTime - booking.StartDateTime).Days;

            bool skipperStatusResult;

            if (totalDays >= 1)
            {
                skipperStatusResult = await CheckSkipperStatusForOverNightAsync(booking.Members);
                if (!skipperStatusResult)
                {
                    return BadRequest("One of the members must have a Skipper Status of Cruise");
                }
            }
            else
            {
                skipperStatusResult = await CheckSkipperStatusForDayAsync(booking.Members);
                if (!skipperStatusResult)
                {
                    return BadRequest("One of the members must have a Skipper Status of Day");
                }
            }


            _context.Bookings.Add(booking);
            await _context.SaveChangesAsync();

            //Charge the credits to each user if there are any credits to charge
            if (booking.CreditsUsed != 0)
            {
                ChargeBookingMemberCredits(booking.Members);
            }

            return CreatedAtAction("GetBooking", new { id = booking.BookingId }, booking);
        }

        // DELETE: api/Bookings/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteBooking([FromRoute] string id)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var booking = await _context.Bookings.Include(m => m.Members).Include(m => m.NonMembers).SingleOrDefaultAsync(m => m.BookingId == id);
            if (booking == null)
            {
                return NotFound();
            }
            RefundBookingMemberCredits(booking.Members);
            //if (booking.Members != null)
            //{
            //    //Drop the old members from the db
            //    bool dropMembersResult = await RemoveOldMembersAsync(booking.BookingId);

            //    if (!dropMembersResult)
            //    {
            //        return BadRequest("unable to drop old members.");
            //    }

            //}

            //if (booking.NonMembers != null)
            //{
            //    //Drop the old non members from the db
            //    bool dropNonMembersResult = await RemoveOldNonMembersAsync(booking.BookingId);

            //    if (!dropNonMembersResult)
            //    {
            //        return BadRequest("unable to drop old non members.");
            //    }
            //}
            _context.Bookings.Remove(booking);
            await _context.SaveChangesAsync();

            return Ok(booking);
        }

        private bool BookingExists(string id)
        {
            return _context.Bookings.Any(e => e.BookingId == id);
        }


        //*************************** HELPER FUNCTIONS *********************************

        /* Function used to validate if credits Charged are the same as Credits Allocated */
        /* Not used at the moment */
        public async Task<bool> compareTotalAllocatedWithTotalCharged(List<Member> members, int totalCharged)
        {
            int userCharged = 0;
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    userCharged += member.AllocatedCredits;
                }
            }

            if (userCharged != totalCharged)
            {
                return false;
            }

            return true;
        }

        public async Task<bool> FullMemberGoodStatusCheckAsync(string userId)
        {
            var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == userId);

            if (user.MemberStatus.Equals("full member good standing", StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public async Task<bool> CheckMembersHaveEnoughCreditsAsync(List<Member> members)
        {
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    if (user.Credits < member.AllocatedCredits)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public async Task<bool> CheckMembersHaveEnoughCreditsForEditAsync(List<Member> members, string bookingId)
        {
            //Grab the old booking and its members from the context
            var oldBooking = await _context.Bookings.SingleOrDefaultAsync(m => m.BookingId == bookingId);

            List<Member> oldMembers = oldBooking.Members;

            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    //Grab the member user
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);

                    //Grab the oldmember if one exists 
                    Member oldMember;

                    if (user == null)
                    {
                        oldMember = null;
                    }
                    else
                    {
                        oldMember = oldMembers.SingleOrDefault(m => m.UserId.Equals(user.Id));
                    }

                    //If there is no old member then check that they have enough credits
                    if (oldMember == null)
                    {
                        if (user.Credits < member.AllocatedCredits)
                        {
                            return false;
                        }

                    }
                    //If there is an old member, add their previously charged credits to their 
                    //current credit and check if they have enough credits for the new allocation
                    else
                    {
                        if ((user.Credits + oldMember.AllocatedCredits) < member.AllocatedCredits)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public async Task RefundAndChargeNewAllocationAsync(List<Member> members, string bookingId)
        {
            //Grab the old booking and its members from the context
            var oldBooking = await _context.Bookings.SingleOrDefaultAsync(m => m.BookingId == bookingId);
            List<Member> oldMembers = oldBooking.Members;
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    //Grab the member user
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);

                    //Grab the oldmember if one exists 
                    var oldMember = oldMembers.SingleOrDefault(m => m.UserId == user.Id);

                    //If there is no old member charge them the credits
                    if (oldMember == null)
                    {
                        user.Credits = user.Credits - member.AllocatedCredits;
                    }
                    //If there is an old member, refund their previously charged credits to their 
                    //current credit and charge them the new amount
                    else
                    {
                        user.Credits = user.Credits + oldMember.AllocatedCredits - member.AllocatedCredits;
                    }
                }
            }
            await _context.SaveChangesAsync();
        }

        public async void ChargeBookingMemberCredits(List<Member> members)
        {
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    user.Credits = user.Credits - member.AllocatedCredits;
                    //await _context.SaveChangesAsync();
                }
            }
        }

        public async void RefundBookingMemberCredits(List<Member> members)
        {
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    user.Credits = user.Credits + member.AllocatedCredits;
                    //await _context.SaveChangesAsync();
                }
            }
        }

        public async Task<bool> CheckSkipperStatusForOverNightAsync(List<Member> members)
        {
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    if (user.SkipperStatus.Equals("cruise skipper", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public async Task<bool> CheckSkipperStatusForDayAsync(List<Member> members)
        {
            if (members.Count() > 0)
            {
                foreach (var member in members)
                {
                    var user = await _context.Users.SingleOrDefaultAsync(m => m.Id == member.UserId);
                    if (user.SkipperStatus.Equals("day skipper", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /* CHECK THIS */
        public int CalculateCredits(DateTime startDate, DateTime endDate, string boatId)
        {

            //Get Boat info
            var boat = _context.Boats.SingleOrDefault(m => m.BoatId.Equals(boatId));
            var creditChargePerHour = boat.CreditsPerHour;

            //Calculate booking info
            var totalHoursOfBooking = (endDate - startDate).TotalHours;

            int totalCredits = 0;

            // *********** Calculate Credits *************

            //Calculate if 24 Hour rule applies
            DateTime currentDay = DateTime.Now;
            DateTime endOfFreePeriod = currentDay.AddDays(1);

            var hoursBeforeBooking = (int)(startDate - currentDay).TotalHours;

            DateTime paidStartDate;
            if (endOfFreePeriod < startDate)
            {
                paidStartDate = startDate;
            }
            else
            {
                paidStartDate = endOfFreePeriod;
            }

            int hoursInFirstDay = 0;
            if (paidStartDate.DayOfYear == endDate.DayOfYear)
            {
                hoursInFirstDay = endDate.Hour - paidStartDate.Hour;
            }
            else
            {
                hoursInFirstDay = 24 - paidStartDate.Hour;
            }

            DayOfWeek startDay = paidStartDate.DayOfWeek;

            if (startDay == DayOfWeek.Saturday || startDay == DayOfWeek.Sunday)
            {
                if (hoursInFirstDay >= 15)
                {
                    totalCredits += 15 * creditChargePerHour;
                }
                else
                {
                    totalCredits += hoursInFirstDay * creditChargePerHour;
                }
            }
            else
            {
                if (hoursInFirstDay >= 10)
                {
                    totalCredits += 10 * creditChargePerHour;
                }
                else
                {
                    totalCredits += hoursInFirstDay * creditChargePerHour;
                }

            }

            //Iterate through dates between start and end date
            var startDayOfYear = paidStartDate.DayOfYear;
            var endDayOfYear = endDate.DayOfYear;

            var tempDayOfYear = startDayOfYear + 1;
            var tempDate = paidStartDate.AddDays(1);

            while (tempDayOfYear < endDayOfYear)
            {

                DayOfWeek day = tempDate.DayOfWeek;

                //Check if its a weekend
                if (day == DayOfWeek.Saturday || day == DayOfWeek.Sunday)
                {
                    totalCredits += 15 * creditChargePerHour;
                }
                else
                {
                    totalCredits += 10 * creditChargePerHour;
                }

                tempDate = tempDate.AddDays(1);
                tempDayOfYear++;
            }

            //Calculate Credits for last day
            //Check if last and first day are the same
            if (paidStartDate.DayOfYear != endDate.DayOfYear)
            {
                DayOfWeek endDay = endDate.DayOfWeek;
                var hoursInLastDay = endDate.Hour;


                if (endDay == DayOfWeek.Saturday || endDay == DayOfWeek.Sunday)
                {
                    if (hoursInLastDay >= 15)
                    {
                        totalCredits += 15 * creditChargePerHour;
                    }
                    else
                    {
                        totalCredits += hoursInLastDay * creditChargePerHour;
                    }
                }
                else
                {
                    if (hoursInLastDay >= 10)
                    {
                        totalCredits += 10 * creditChargePerHour;
                    }
                    else
                    {
                        totalCredits += hoursInLastDay * creditChargePerHour;
                    }
                }
            }
            return totalCredits;
        }

        public async Task<bool> CheckBoatIsInGoodStatusAsync(string boatId)
        {
            var boat = await _context.Boats.SingleOrDefaultAsync(b => b.BoatId.Equals(boatId));

            if (boat.Status.Equals("operational", StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private List<DateTime> CreateSemiHourlyList(DateTime selectedTime)
        {
            List<DateTime> list = new List<DateTime>();
            list.Add(selectedTime);

            for (int i = 0; i < 23; i++)
            {
                selectedTime = selectedTime.AddHours(1);
                list.Add(selectedTime);
            }

            return list;
        }

        private List<DateTime> CreateSemiHourlyListWithRange(DateTime startTime, DateTime endTime)
        {
            List<DateTime> list = new List<DateTime>();

            while (startTime != endTime)
            {
                startTime = startTime.AddHours(1);
                list.Add(startTime);
            }

            return list;
        }


        public async Task<bool> RemoveOldMembersAsync(string bookingId)
        {
            var members = _context.Members.Where(m => m.BookingId == bookingId);
            foreach (var member in members)
            {
                var curMember = await _context.Members.Where(m => m.BookingId.Equals(bookingId)).SingleOrDefaultAsync(m => m.UserId.Equals(member.UserId));
                if (curMember == null)
                {
                    return false;
                }
                _context.Members.Remove(curMember);

                await _context.SaveChangesAsync();
            }
            return true;
        }

        public async Task<bool> RemoveOldNonMembersAsync(string bookingId)
        {
            var members = _context.NonMembers.Where(m => m.BookingId == bookingId);

            foreach (var member in members)
            {
                var curMember = await _context.NonMembers.Where(m => m.BookingId.Equals(bookingId)).SingleOrDefaultAsync(m => m.NonMemberId.Equals(member.NonMemberId));
                if (curMember == null)
                {
                    return false;
                }
                _context.NonMembers.Remove(curMember);

            }
            await _context.SaveChangesAsync();
            return true;
        }

        private Boolean IsValidDateRange(DateTime startTime, DateTime endTime)
        {
            return endTime > startTime;
        }

        private Boolean IsValidTimeSpan(DateTime startTime, DateTime endTime)
        {
            TimeSpan diff = endTime.Subtract(startTime);

            return (int)diff.TotalHours <= 72;
        }

        private async Task<Boolean> IsValidBookingDateRange(string boatId, DateTime startTime, DateTime endTime)
        {
            DateTime nextStartDate = await _context.Bookings.Where(d => d.StartDateTime > startTime
                && d.BoatId == boatId && d.StartDateTime < endTime)
                .Select(s => s.StartDateTime).FirstOrDefaultAsync();

            return nextStartDate == null;
        }

    }
}