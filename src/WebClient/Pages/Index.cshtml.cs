﻿using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebClient.Pages
{
    public class IndexModel : PageModel
    {
        public IndexModel()
        {
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public void OnGet()
        {

        }

        public IActionResult OnPost() => RedirectToPage("hello", new { username = Input.Username });



        public class InputModel
        {
            [Required]
            public string Username { get; set; }

        }
    }
}
