﻿using System;
using System.IO;
using System.Linq;

namespace MyNUnit
{
    class Program
    {
        /// <summary>
        /// Пример работы MyNUnit с выводом результатов на экран
        /// </summary>
        static void Main(string[] args)
        {
            var root = "..\\..\\..\\TestProjects\\TestResult\\Assembly";

            MyNUnit.RunTests(root);
        }
    }
}