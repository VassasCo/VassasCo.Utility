// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using VassasCo.Utility;

namespace VassasCo.Utility.Tests
{
    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string IdCard { get; set; } = "";
        public long BigNumber { get; set; }
        public bool Active { get; set; }
        public char Grade { get; set; }
        public int Score { get; set; }
        public byte[]? Thumbnail { get; set; }
        public List<Address> Addresses { get; set; } = new List<Address>();
        public Department? Department { get; set; }
    }

    public class Address
    {
        public string City { get; set; } = "";
        public string Street { get; set; } = "";
    }

    public class Department
    {
        public string DeptName { get; set; } = "";
        public Person? Manager { get; set; }
    }

    public enum Status
    {
        [Description("在职")]
        Active = 1,

        [Description("离职")]
        Resigned = 2
    }

    public class FlatRow
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public bool Active { get; set; }
        public DateTime Birthday { get; set; }
        public Status Status { get; set; }
        public string? Remark { get; set; }
    }

    public class OrderContainer
    {
        public int Id { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<int> Codes { get; set; } = new List<int>();
    }

    public static class SampleData
    {
        public static List<Person> People(int count = 5)
        {
            var list = new List<Person>(count);
            for (int i = 1; i <= count; i++)
            {
                list.Add(new Person
                {
                    Id = i,
                    Name = "用户" + i,
                    IdCard = "31010119900101001" + (i % 10),
                    BigNumber = 9007199254740993L + i,
                    Active = i % 2 == 0,
                    Grade = (char)('A' + i),
                    Score = 55 + i * 10,
                    Addresses = new List<Address>
                    {
                        new Address { City = "上海", Street = "南京路" + i + "号" }
                    }
                });
            }
            return list;
        }
    }
}
